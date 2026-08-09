using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Infrastructure.Docker;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.DockerInstances;

/// <summary>
/// V3.5 — endpoint tests for <see cref="DockerInstancesController"/>.
/// Covers the container-list shape (watch-link join), each lifecycle
/// action's success + error envelope, the <c>AllowContainerRemoval</c>
/// feature-flag gate, the owner-scoping check, and the audit-row
/// invariants the per-watch history view depends on.
/// </summary>
public class DockerInstancesControllerTests : IAsyncLifetime
{
    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly Mock<IDockerHostClient> _hostClientMock = new();
    private readonly Mock<IDockerLogStreamer> _logStreamerMock = new();
    private readonly Mock<IDockerStatsStreamer> _statsStreamerMock = new();
    private readonly Mock<IDockerProjectUpdater> _projectUpdaterMock = new();
    // Default (unconfigured) IsSelfInProjectAsync returns false, so existing
    // "Update project" tests keep exercising the in-process bulk path.
    private readonly Mock<ISelfUpdateLauncher> _selfUpdateLauncherMock = new();
    private readonly Mock<IDockerUpdateChecker> _updateCheckerMock = new();
    private readonly Mock<IDockerPruneRunner> _pruneRunnerMock = new();
    private readonly Mock<IContainerIconResolver> _iconResolverMock = new();
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public DockerInstancesControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        // Default re-check: every watch is reported up-to-date so the per-test
        // setups don't have to wire this when they only care about the bulk
        // update flow. Individual tests can override.
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpToDate,
                CurrentDigest: "sha256:new", LatestDigest: "sha256:new",
                CurrentVersionTag: "v1", LatestVersionTag: "v1",
                Error: null, CheckedAtUtc: DateTime.UtcNow));

        // Default: no official icon resolves. Icon-specific tests override.
        _iconResolverMock
            .Setup(r => r.ResolveIconForImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
        _dataFactory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, Guid.Empty);
        var seeder = new UserSeeder(_dataFactory);
        await seeder.SeedAsync();
        _userId = seeder.Owner.Id;
        _otherUserId = seeder.Other.Id;
        _dataFactory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, _userId);
    }

    public async ValueTask DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    [Fact]
    public async Task ListContainers_ReturnsCards_LinkingTheUsersWatch()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, conn.Id, containerName: "wp");

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc123", name: "wp", image: "wordpress:6", state: "running"),
                MakeDetail(id: "def456", name: "redis", image: "redis:7", state: "running"),
            });

        var ctrl = BuildController();
        var result = await ctrl.ListContainers(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsAssignableFrom<List<DockerContainerCard>>(ok.Value);
        Assert.Equal(2, cards.Count);

        var wpCard = cards.Single(c => c.Name == "wp");
        Assert.Equal(watch.Id, wpCard.WatchId);
        Assert.Equal(svc.Id, wpCard.WebResourceId);

        var redisCard = cards.Single(c => c.Name == "redis");
        Assert.Null(redisCard.WatchId);
        Assert.Null(redisCard.WebResourceId);
    }

    [Fact]
    public async Task ListContainers_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.ListContainers(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.ListContainerDetailsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V7.8 — container icons ───────────────────────────────────────────────

    [Fact]
    public async Task ListContainers_SetsIconDataUri_CustomThenOfficialThenNull()
    {
        var conn = await SeedConnectionAsync(_userId);

        // "wp" has a custom upload; "redis" resolves an official icon; "mystery"
        // resolves neither.
        _dbContext.ContainerIcons.Add(new ContainerIconEntity
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            DockerConnectionId = conn.Id,
            ContainerName = "wp",
            IconSource = ContainerIconSource.Custom,
            LogoBase64 = "data:image/png;base64,CUSTOM",
            CustomLogoPath = "/uploads/container-icons/wp.png",
            CreatedUtc = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _iconResolverMock
            .Setup(r => r.ResolveIconForImageAsync("redis:7", It.IsAny<CancellationToken>()))
            .ReturnsAsync("data:image/webp;base64,OFFICIAL");

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "a", name: "wp", image: "wordpress:6", state: "running"),
                MakeDetail(id: "b", name: "redis", image: "redis:7", state: "running"),
                MakeDetail(id: "c", name: "mystery", image: "obscure/thing:1", state: "running"),
            });

        var ctrl = BuildController();
        var result = await ctrl.ListContainers(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cards = Assert.IsAssignableFrom<List<DockerContainerCard>>(ok.Value);
        Assert.Equal("data:image/png;base64,CUSTOM", cards.Single(c => c.Name == "wp").IconDataUri);
        Assert.Equal("data:image/webp;base64,OFFICIAL", cards.Single(c => c.Name == "redis").IconDataUri);
        Assert.Null(cards.Single(c => c.Name == "mystery").IconDataUri);

        // The resolver is never consulted for the container that already has a
        // custom upload.
        _iconResolverMock.Verify(r => r.ResolveIconForImageAsync("wordpress:6", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadIcon_WritesCustomRow_WithBase64()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.UploadIcon(conn.Id, "wp", PngRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        var row = await _dbContext.ContainerIcons.AsNoTracking()
            .SingleAsync(i => i.DockerConnectionId == conn.Id && i.ContainerName == "wp");
        Assert.Equal(ContainerIconSource.Custom, row.IconSource);
        Assert.StartsWith("data:image/png;base64,", row.LogoBase64);
        Assert.Equal(_userId, row.UserId);
    }

    [Fact]
    public async Task UploadIcon_SecondUpload_UpdatesRowInPlace()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        await ctrl.UploadIcon(conn.Id, "wp", PngRequest("first"), CancellationToken.None);
        await ctrl.UploadIcon(conn.Id, "wp", WebpRequest("second"), CancellationToken.None);

        var rows = await _dbContext.ContainerIcons.AsNoTracking()
            .Where(i => i.DockerConnectionId == conn.Id && i.ContainerName == "wp")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.StartsWith("data:image/webp;base64,", row.LogoBase64);
    }

    [Fact]
    public async Task UploadIcon_RejectsNonImage()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.UploadIcon(conn.Id, "wp",
            new ContainerIconUploadRequest("data:application/pdf;base64,JVBERi0="), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, await _dbContext.ContainerIcons.CountAsync());
    }

    [Fact]
    public async Task UploadIcon_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.UploadIcon(conn.Id, "wp", PngRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, await _dbContext.ContainerIcons.CountAsync());
    }

    [Fact]
    public async Task ResetIcon_RemovesRow_AndRevertsToAuto()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();
        await ctrl.UploadIcon(conn.Id, "wp", PngRequest(), CancellationToken.None);
        Assert.Equal(1, await _dbContext.ContainerIcons.CountAsync());

        var result = await ctrl.ResetIcon(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, await _dbContext.ContainerIcons.CountAsync());
    }

    [Fact]
    public async Task ResetIcon_IsIdempotent_WhenNoOverrideExists()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.ResetIcon(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ResetIcon_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.ResetIcon(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static ContainerIconUploadRequest PngRequest(string content = "fake-image-data") =>
        new($"data:image/png;base64,{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content))}");

    private static ContainerIconUploadRequest WebpRequest(string content = "fake-image-data") =>
        new($"data:image/webp;base64,{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content))}");

    [Fact]
    public async Task Start_WritesSuccessAuditRow_OnOk()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.StartContainerAsync(It.IsAny<DockerHostTransport>(), "redis", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "deadbeef", null));

        var ctrl = BuildController();
        var result = await ctrl.Start(conn.Id, "redis", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Start, response.Attempt.ActionType);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Attempt.Status);
        Assert.Equal("deadbeef", response.Attempt.ContainerId);
        Assert.Equal(conn.Id, response.Attempt.DockerConnectionId);
        // No watch links — redis isn't tracked.
        Assert.Null(response.Attempt.DockerWatchId);
        Assert.Null(response.Attempt.WebResourceId);

        var persisted = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(DockerContainerActionType.Start, persisted.ActionType);
        Assert.Equal("redis", persisted.ContainerName);
    }

    [Fact]
    public async Task Stop_LinksWatch_WhenContainerIsTracked()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, conn.Id, containerName: "wp");

        _hostClientMock
            .Setup(h => h.StopContainerAsync(It.IsAny<DockerHostTransport>(), "wp", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "deadbeef", null));

        var ctrl = BuildController();
        var result = await ctrl.Stop(conn.Id, "wp", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(watch.Id, response.Attempt.DockerWatchId);
        Assert.Equal(svc.Id, response.Attempt.WebResourceId);
        Assert.Equal(DockerContainerActionType.Stop, response.Attempt.ActionType);
        Assert.Equal(watch.ImageReference, response.Attempt.ImageReference);
    }

    [Fact]
    public async Task Restart_WritesFailureAuditRow_AndReturns502_WhenHostUnreachable()
    {
        var conn = await SeedConnectionAsync(_userId);

        _hostClientMock
            .Setup(h => h.RestartContainerAsync(It.IsAny<DockerHostTransport>(), "wp", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.HostUnreachable, null, "dns fail"));

        var ctrl = BuildController();
        var result = await ctrl.Restart(conn.Id, "wp", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);

        var persisted = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(DockerUpdateAttemptStatus.HostUnreachable, persisted.Status);
        Assert.Equal("dns fail", persisted.Error);
    }

    [Fact]
    public async Task Remove_Returns403_WhenFeatureFlagDisabled_AndContainerRunning()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc123", name: "wp", image: "wordpress:6", state: "running"),
            });

        var ctrl = BuildController(allowRemoval: false);
        var result = await ctrl.Remove(conn.Id, "wp", force: false, deleteProjectFolder: false, cancellationToken: CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        _hostClientMock.Verify(h => h.RemoveContainerAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, await _dbContext.DockerUpdateAttempts.CountAsync());
    }

    [Fact]
    public async Task Remove_AllowsExitedContainer_WhenFeatureFlagDisabled()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc123", name: "db", image: "postgres:16", state: "exited"),
            });
        _hostClientMock
            .Setup(h => h.RemoveContainerAsync(It.IsAny<DockerHostTransport>(), "db", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "abc123", null));

        var ctrl = BuildController(allowRemoval: false);
        var result = await ctrl.Remove(conn.Id, "db", force: false, deleteProjectFolder: false, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Remove, response.Attempt.ActionType);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Attempt.Status);

        var persisted = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(DockerContainerActionType.Remove, persisted.ActionType);
    }

    [Fact]
    public async Task Remove_AllowsDeadContainer_WhenFeatureFlagDisabled()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "def456", name: "app", image: "myapp:latest", state: "dead"),
            });
        _hostClientMock
            .Setup(h => h.RemoveContainerAsync(It.IsAny<DockerHostTransport>(), "app", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "def456", null));

        var ctrl = BuildController(allowRemoval: false);
        var result = await ctrl.Remove(conn.Id, "app", force: false, deleteProjectFolder: false, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Remove, response.Attempt.ActionType);
    }

    [Fact]
    public async Task Remove_Succeeds_WhenFeatureFlagEnabled()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.RemoveContainerAsync(It.IsAny<DockerHostTransport>(), "redis", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "deadbeef", null));

        var ctrl = BuildController(allowRemoval: true);
        var result = await ctrl.Remove(conn.Id, "redis", force: false, deleteProjectFolder: false, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Remove, response.Attempt.ActionType);
    }

    // ── V8.2 — deleteProjectFolder ───────────────────────────────────────────

    [Fact]
    public async Task Remove_DeleteProjectFolder_RejectsNonSshHost()
    {
        var conn = await SeedConnectionAsync(_userId, hostType: DockerHostType.LocalSocket);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc", name: "web", image: "nginx:1", state: "running",
                    composeProject: "myproject",
                    labels: new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = "/srv/myproject" }),
            });

        var ctrl = BuildController(allowRemoval: true);
        var result = await ctrl.Remove(conn.Id, "web", force: true, deleteProjectFolder: true, cancellationToken: CancellationToken.None);

        var obj = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        _hostClientMock.Verify(h => h.RemoveContainerAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _hostClientMock.Verify(h => h.DeleteHostDirectoryAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Remove_DeleteProjectFolder_RejectsContainerWithoutComposeProject()
    {
        var conn = await SeedConnectionAsync(_userId, hostType: DockerHostType.Ssh);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc", name: "standalone", image: "nginx:1", state: "running"),
            });

        var ctrl = BuildController(allowRemoval: true);
        var result = await ctrl.Remove(conn.Id, "standalone", force: true, deleteProjectFolder: true, cancellationToken: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _hostClientMock.Verify(h => h.DeleteHostDirectoryAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Remove_DeleteProjectFolder_DeletesFolder_OnSshHost_AfterContainerRemoved()
    {
        var conn = await SeedConnectionAsync(_userId, hostType: DockerHostType.Ssh);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc", name: "web", image: "nginx:1", state: "running",
                    composeProject: "myproject",
                    labels: new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = "/srv/myproject" }),
            });
        _hostClientMock
            .Setup(h => h.RemoveContainerAsync(It.IsAny<DockerHostTransport>(), "web", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "abc", null));
        _hostClientMock
            .Setup(h => h.DeleteHostDirectoryAsync(It.IsAny<DockerHostTransport>(), "/srv/myproject", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, null, null));

        var ctrl = BuildController(allowRemoval: true);
        var result = await ctrl.Remove(conn.Id, "web", force: true, deleteProjectFolder: true, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.True(response.ProjectFolderDeleted);
        Assert.Null(response.ProjectFolderError);
        _hostClientMock.Verify(h => h.DeleteHostDirectoryAsync(
            It.IsAny<DockerHostTransport>(), "/srv/myproject", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Remove_DeleteProjectFolder_ReportsFailure_ButContainerStaysRemoved()
    {
        var conn = await SeedConnectionAsync(_userId, hostType: DockerHostType.Ssh);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "abc", name: "web", image: "nginx:1", state: "running",
                    composeProject: "myproject",
                    labels: new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = "/srv/myproject" }),
            });
        _hostClientMock
            .Setup(h => h.RemoveContainerAsync(It.IsAny<DockerHostTransport>(), "web", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.Ok, "abc", null));
        _hostClientMock
            .Setup(h => h.DeleteHostDirectoryAsync(It.IsAny<DockerHostTransport>(), "/srv/myproject", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.ActionFailed, null, "permission denied"));

        var ctrl = BuildController(allowRemoval: true);
        var result = await ctrl.Remove(conn.Id, "web", force: true, deleteProjectFolder: true, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Remove, response.Attempt.ActionType);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Attempt.Status);
        Assert.False(response.ProjectFolderDeleted);
        Assert.Equal("permission denied", response.ProjectFolderError);
    }

    [Fact]
    public async Task Start_Returns404_WhenContainerMissing()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.StartContainerAsync(It.IsAny<DockerHostTransport>(), "ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerActionResult(DockerHostStatus.ContainerNotFound, null, "missing"));

        var ctrl = BuildController();
        var result = await ctrl.Start(conn.Id, "ghost", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Stop_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.Stop(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.StopContainerAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── V3.5 — instance-scoped diagnostics endpoints ─────────────────────────

    [Fact]
    public async Task Inspect_ReturnsPayload_OnSuccess()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), "wp", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerInspectResult(DockerHostStatus.Ok, MakeInspect(name: "wp"), null));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(conn.Id, "wp", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DockerContainerInspect>(ok.Value);
        Assert.Equal("wp", dto.Name);
    }

    [Fact]
    public async Task Inspect_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.Inspect(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.InspectContainerAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Inspect_Returns404_WhenContainerMissingOnHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), "ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerInspectResult(DockerHostStatus.ContainerNotFound, null, "no such container"));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(conn.Id, "ghost", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Logs_StreamsNdjson_OnSuccess()
    {
        var conn = await SeedConnectionAsync(_userId);
        var lines = new[]
        {
            new DockerLogLine(DockerLogStreamChannel.Stdout, new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc), "boot"),
            new DockerLogLine(DockerLogStreamChannel.Stderr, null, "warn"),
        };
        _logStreamerMock
            .Setup(s => s.StreamLogsAsync(
                It.IsAny<DockerHostTransport>(), "wp", It.IsAny<DockerLogStreamRequest>(),
                It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DockerHostTransport _, string _, DockerLogStreamRequest _,
                Func<DockerLogLine, CancellationToken, Task> onLine, CancellationToken ct) =>
            {
                foreach (var line in lines) await onLine(line, ct);
                return DockerLogStreamResult.Ok;
            });

        var ctrl = BuildController();
        var (body, response) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamLogs(conn.Id, "wp",
            follow: false, tail: 200, since: null,
            timestamps: true, stdout: true, stderr: true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("application/x-ndjson", response.ContentType);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"stream\":\"stdout\"", text);
        Assert.Contains("\"message\":\"boot\"", text);
        Assert.Contains("\"stream\":\"stderr\"", text);
    }

    [Fact]
    public async Task Logs_Returns400_WhenBothStreamsDisabled()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();
        var result = await ctrl.StreamLogs(conn.Id, "wp",
            follow: false, tail: 200, since: null,
            timestamps: true, stdout: false, stderr: false, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
        _logStreamerMock.Verify(s => s.StreamLogsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerLogStreamRequest>(),
            It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Logs_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.StreamLogs(conn.Id, "wp",
            follow: false, tail: 200, since: null,
            timestamps: true, stdout: true, stderr: true, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Stats_StreamsNdjson_OnSuccess()
    {
        var conn = await SeedConnectionAsync(_userId);
        var sample = new DockerContainerStatsSample(
            TimestampUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
            CpuPercent: 25.5,
            MemoryUsageBytes: 100UL * 1024 * 1024,
            MemoryLimitBytes: 512UL * 1024 * 1024,
            MemoryPercent: 19.5,
            NetworkRxBytes: 1024, NetworkTxBytes: 512,
            BlockReadBytes: 0, BlockWriteBytes: 0,
            OnlineCpus: 4);
        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(), "wp", It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DockerHostTransport _, string _, DockerStatsStreamRequest _,
                Func<DockerContainerStatsSample, CancellationToken, Task> onSample, CancellationToken ct) =>
            {
                await onSample(sample, ct);
                return DockerStatsStreamResult.Ok;
            });

        var ctrl = BuildController();
        var (body, response) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamStats(conn.Id, "wp", oneShot: false, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("application/x-ndjson", response.ContentType);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"cpuPercent\":25.5", text);
        Assert.Contains("\"onlineCpus\":4", text);
    }

    [Fact]
    public async Task Stats_ForwardsOneShotFlag()
    {
        var conn = await SeedConnectionAsync(_userId);
        DockerStatsStreamRequest? captured = null;
        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((DockerHostTransport _, string _, DockerStatsStreamRequest req,
                Func<DockerContainerStatsSample, CancellationToken, Task> _, CancellationToken _) =>
            {
                captured = req;
                return Task.FromResult(DockerStatsStreamResult.Ok);
            });

        var ctrl = BuildController();
        AttachResponseBuffer(ctrl);
        await ctrl.StreamStats(conn.Id, "wp", oneShot: true, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.False(captured!.Stream);
        Assert.True(captured.OneShot);
    }

    [Fact]
    public async Task Stats_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.StreamStats(conn.Id, "wp", oneShot: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static DockerContainerInspect MakeInspect(string name) => new(
        Id: "abcd1234",
        Name: name,
        Image: "ghcr.io/owner/repo:v1",
        ImageId: "sha256:cafebabe",
        ImageRepoDigests: Array.Empty<string>(),
        CreatedUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        RestartCount: 0,
        Platform: "linux",
        Driver: "overlay2",
        State: new DockerInspectState("running", true, false, false, false, false, 0, null,
            new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc), null, null),
        Config: new DockerInspectConfig(null, null, null, "ghcr.io/owner/repo:v1",
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<DockerInspectEnvVar>(),
            new Dictionary<string, string>(),
            Array.Empty<string>()),
        HostConfig: new DockerInspectHostConfig("bridge", null, null, null, false, false, false,
            Array.Empty<DockerInspectPortBinding>()),
        NetworkSettings: new DockerInspectNetworkSettings(new Dictionary<string, DockerInspectNetwork>()),
        Mounts: Array.Empty<DockerInspectMount>());

    private static (MemoryStream Body, HttpResponse Response) AttachResponseBuffer(DockerInstancesController ctrl)
    {
        var body = new MemoryStream();
        var httpContext = ctrl.ControllerContext.HttpContext;
        httpContext.Response.Body = body;
        httpContext.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return (body, httpContext.Response);
    }

    // ── V5.4 — Compose project bulk update ──────────────────────────────────

    [Fact]
    public async Task UpdateProject_HappyPath_WritesParentAndChildAuditRows()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "myproject-web-1", image: "nginx:1.27", state: "running",
                    composeProject: "myproject", composeService: "web"),
                MakeDetail(id: "id-db", name: "myproject-db-1", image: "postgres:16", state: "running",
                    composeProject: "myproject", composeService: "db"),
                MakeDetail(id: "id-other", name: "redis", image: "redis:7", state: "running"),
            });

        _projectUpdaterMock
            .Setup(u => u.UpdateProjectAsync(It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerProjectUpdateProfile p, CancellationToken _) =>
                new DockerProjectUpdateResult(
                    DockerProjectUpdateMode.Compose,
                    DockerUpdateAttemptStatus.Success,
                    null,
                    p.Services.Select(s => new DockerProjectServiceResult(
                        s.ServiceName, s.ContainerName, s.WatchId, s.WebResourceId,
                        s.UpdateProfile.ImageReference,
                        DockerUpdateAttemptStatus.Success,
                        "sha256:old", "sha256:new", null,
                        HealthVerified: true, HealthVerifiedUtc: DateTime.UtcNow)).ToList()));

        var ctrl = BuildController();
        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerProjectUpdateResponse>(ok.Value);
        Assert.Equal("Compose", response.Mode);
        Assert.Equal(DockerContainerActionType.UpdateProject, response.Parent.ActionType);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Parent.Status);
        Assert.Equal(2, response.Services.Count);
        Assert.All(response.Services, s => Assert.Equal(DockerContainerActionType.Update, s.ActionType));

        // Persisted: one parent + two children.
        var attempts = await _dbContext.DockerUpdateAttempts.AsNoTracking().ToListAsync();
        Assert.Equal(3, attempts.Count);
        var parent = attempts.Single(a => a.ActionType == DockerContainerActionType.UpdateProject);
        Assert.Equal("myproject", parent.ComposeProject);
        var children = attempts.Where(a => a.ActionType == DockerContainerActionType.Update).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, c =>
        {
            Assert.Equal(parent.Id, c.ParentAttemptId);
            Assert.Equal("myproject", c.ComposeProject);
        });
    }

    [Fact]
    public async Task UpdateProject_LinksTrackedWatchOnChildRow()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, conn.Id, containerName: "myproject-web-1");

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "myproject-web-1", image: "nginx:1.27", state: "running",
                    composeProject: "myproject", composeService: "web"),
            });

        DockerProjectUpdateProfile? captured = null;
        _projectUpdaterMock
            .Setup(u => u.UpdateProjectAsync(It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerProjectUpdateProfile p, CancellationToken _) =>
            {
                captured = p;
                return new DockerProjectUpdateResult(
                    DockerProjectUpdateMode.Compose,
                    DockerUpdateAttemptStatus.Success, null,
                    p.Services.Select(s => new DockerProjectServiceResult(
                        s.ServiceName, s.ContainerName, s.WatchId, s.WebResourceId,
                        s.UpdateProfile.ImageReference,
                        DockerUpdateAttemptStatus.Success, null, "sha256:new", null,
                        HealthVerified: true, HealthVerifiedUtc: DateTime.UtcNow)).ToList());
            });

        var ctrl = BuildController();
        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        // The watched service carried its watch + service link through to the
        // updater, and the persisted child row picked them up.
        Assert.NotNull(captured);
        var target = Assert.Single(captured!.Services);
        Assert.Equal(watch.Id, target.WatchId);
        Assert.Equal(svc.Id, target.WebResourceId);

        var child = await _dbContext.DockerUpdateAttempts.AsNoTracking()
            .SingleAsync(a => a.ActionType == DockerContainerActionType.Update);
        Assert.Equal(watch.Id, child.DockerWatchId);
        Assert.Equal(svc.Id, child.WebResourceId);
    }

    [Fact]
    public async Task UpdateProject_PartialFailure_Returns502WithParentAndChildren()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "myproject-web-1", image: "nginx:1.27", state: "running",
                    composeProject: "myproject", composeService: "web"),
                MakeDetail(id: "id-db", name: "myproject-db-1", image: "postgres:16", state: "running",
                    composeProject: "myproject", composeService: "db"),
            });

        _projectUpdaterMock
            .Setup(u => u.UpdateProjectAsync(It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerProjectUpdateProfile p, CancellationToken _) =>
                new DockerProjectUpdateResult(
                    DockerProjectUpdateMode.Recreate,
                    DockerUpdateAttemptStatus.RecreateFailed,
                    "1 of 2 services failed to update.",
                    new[]
                    {
                        new DockerProjectServiceResult("web", "myproject-web-1", null, null, "nginx:1.27",
                            DockerUpdateAttemptStatus.Success, "sha256:old", "sha256:new", null, true, DateTime.UtcNow),
                        new DockerProjectServiceResult("db", "myproject-db-1", null, null, "postgres:16",
                            DockerUpdateAttemptStatus.PullFailed, "sha256:old-db", null, "manifest unknown", false, null),
                    }));

        var ctrl = BuildController();
        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        var response = Assert.IsType<DockerProjectUpdateResponse>(obj.Value);
        Assert.Equal("Recreate", response.Mode);
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, response.Parent.Status);
        Assert.Equal(2, response.Services.Count);
        Assert.Equal(DockerUpdateAttemptStatus.PullFailed,
            response.Services.Single(s => s.ContainerName == "myproject-db-1").Status);

        // All three rows are persisted regardless of the 502.
        Assert.Equal(3, await _dbContext.DockerUpdateAttempts.CountAsync());
    }

    [Fact]
    public async Task UpdateProject_ReChecksTrackedWatchesOnSuccess_SoUpdateBadgeClears()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync();
        // Seed the watch with UpdateAvailable so we can verify the re-check
        // flips it back to UpToDate after the bulk update.
        var watch = await SeedWatchAsync(svc.Id, _userId, conn.Id, containerName: "myproject-web-1");
        await _dbContext.DockerWatches
            .Where(w => w.Id == watch.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.UpdateStatus, DockerUpdateStatus.UpdateAvailable)
                .SetProperty(w => w.CurrentDigest, "sha256:old")
                .SetProperty(w => w.LatestDigest, "sha256:new"));

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "myproject-web-1", image: "nginx:1.27", state: "running",
                    composeProject: "myproject", composeService: "web"),
            });

        _projectUpdaterMock
            .Setup(u => u.UpdateProjectAsync(It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerProjectUpdateProfile p, CancellationToken _) =>
                new DockerProjectUpdateResult(
                    DockerProjectUpdateMode.Compose,
                    DockerUpdateAttemptStatus.Success, null,
                    p.Services.Select(s => new DockerProjectServiceResult(
                        s.ServiceName, s.ContainerName, s.WatchId, s.WebResourceId,
                        s.UpdateProfile.ImageReference,
                        DockerUpdateAttemptStatus.Success, "sha256:old", "sha256:new", null,
                        HealthVerified: true, HealthVerifiedUtc: DateTime.UtcNow)).ToList()));

        var ctrl = BuildController();
        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);

        // Default checker stub returns UpToDate → watch should flip.
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.Is<DockerWatchProfile>(p => p.ContainerName == "myproject-web-1"),
            It.IsAny<CancellationToken>()), Times.Once);

        var refreshed = await _dbContext.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == watch.Id);
        Assert.Equal(DockerUpdateStatus.UpToDate, refreshed.UpdateStatus);
        Assert.Equal("sha256:new", refreshed.CurrentDigest);
    }

    [Fact]
    public async Task UpdateProject_SkipsReCheckForFailedServicesAndUntrackedContainers()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync();
        var watchOk = await SeedWatchAsync(svc.Id, _userId, conn.Id, containerName: "myproject-web-1");
        var svc2 = await _dataFactory.ServiceAsync();
        var watchFailed = await SeedWatchAsync(svc2.Id, _userId, conn.Id, containerName: "myproject-db-1");

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "myproject-web-1", image: "nginx:1.27", state: "running",
                    composeProject: "myproject", composeService: "web"),
                MakeDetail(id: "id-db", name: "myproject-db-1", image: "postgres:16", state: "running",
                    composeProject: "myproject", composeService: "db"),
                MakeDetail(id: "id-cache", name: "myproject-cache-1", image: "redis:7", state: "running",
                    composeProject: "myproject", composeService: "cache"),
            });

        _projectUpdaterMock
            .Setup(u => u.UpdateProjectAsync(It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerProjectUpdateProfile p, CancellationToken _) =>
                new DockerProjectUpdateResult(
                    DockerProjectUpdateMode.Compose,
                    DockerUpdateAttemptStatus.RecreateFailed, "1 of 3 services failed",
                    new[]
                    {
                        // web: tracked + succeeded → should re-check
                        new DockerProjectServiceResult("web", "myproject-web-1", watchOk.Id, svc.Id,
                            "nginx:1.27", DockerUpdateAttemptStatus.Success, "sha256:old", "sha256:new", null, true, DateTime.UtcNow),
                        // db: tracked but FAILED → must NOT re-check
                        new DockerProjectServiceResult("db", "myproject-db-1", watchFailed.Id, svc2.Id,
                            "postgres:16", DockerUpdateAttemptStatus.PullFailed, "sha256:old-db", null, "manifest unknown", false, null),
                        // cache: untracked → no watch FK, nothing to re-check
                        new DockerProjectServiceResult("cache", "myproject-cache-1", null, null,
                            "redis:7", DockerUpdateAttemptStatus.Success, "sha256:old-cache", "sha256:new-cache", null, true, DateTime.UtcNow),
                    }));

        var ctrl = BuildController();
        await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        // Only the watched + succeeded service triggers a re-check.
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Once);
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.Is<DockerWatchProfile>(p => p.ContainerName == "myproject-web-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProject_Returns404_WhenNoContainersMatchProject()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail>
            {
                MakeDetail(id: "id-web", name: "redis", image: "redis:7", state: "running"),
            });

        var ctrl = BuildController();
        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        _projectUpdaterMock.Verify(u => u.UpdateProjectAsync(
            It.IsAny<DockerProjectUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, await _dbContext.DockerUpdateAttempts.CountAsync());
    }

    [Fact]
    public async Task UpdateProject_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.UpdateProject(conn.Id, "myproject", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.ListContainerDetailsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V5.5 — storage + prune endpoints ────────────────────────────────────

    [Fact]
    public async Task GetImageStorage_ReturnsCountsAndRecentRuns()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.GetImageStorageAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerImageStorageResult(
                DockerHostStatus.Ok,
                TotalImageCount: 42,
                DanglingImageCount: 5,
                DanglingImageBytes: 1_000_000,
                UnusedImageCount: 7,
                UnusedImageBytes: 2_000_000,
                Images: new List<DockerImageSummary>
                {
                    new("sha256:aaaa", new[] { "nginx:1.27" }, 2_000_000, DateTime.UtcNow,
                        IsDangling: false, IsUnused: true, UsedByContainers: Array.Empty<string>()),
                    new("sha256:bbbb", Array.Empty<string>(), 1_000_000, DateTime.UtcNow,
                        IsDangling: true, IsUnused: true, UsedByContainers: Array.Empty<string>()),
                },
                Error: null));

        // Seed one prune run so the response carries it back.
        _dbContext.DockerPruneRuns.Add(new DockerPruneRunEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = conn.Id,
            InitiatedByUserId = _userId,
            Trigger = DockerPruneTrigger.Manual,
            Status = DockerPruneStatus.Success,
            ImagesDeleted = 2,
            SpaceReclaimedBytes = 500_000,
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-4),
            CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var result = await ctrl.GetImageStorage(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DockerImageStorageResponse>(ok.Value);
        Assert.Equal(42, body.TotalImageCount);
        Assert.Equal(5, body.DanglingImageCount);
        Assert.Equal(1_000_000, body.DanglingImageBytes);
        Assert.Equal(7, body.UnusedImageCount);
        Assert.True(body.AllowImagePrune); // default for newly seeded connections
        Assert.Single(body.RecentRuns);
        Assert.Equal(2, body.RecentRuns[0].ImagesDeleted);

        // V5.5 — the per-image drill-down list rides along.
        Assert.Equal(2, body.Images.Count);
        var dangling = Assert.Single(body.Images, i => i.IsDangling);
        Assert.Empty(dangling.RepoTags);
        Assert.Contains(body.Images, i => i.RepoTags.Contains("nginx:1.27"));
    }

    [Fact]
    public async Task GetImageStorage_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.GetImageStorage(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.GetImageStorageAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PruneImages_WritesAuditRow_AndAdvancesLastPruneOnSuccess()
    {
        var conn = await SeedConnectionAsync(_userId);
        var completedUtc = DateTime.UtcNow;
        _pruneRunnerMock
            .Setup(r => r.RunAsync(
                It.Is<DockerPruneRequest>(req =>
                    req.ConnectionId == conn.Id
                    && req.Trigger == DockerPruneTrigger.Manual
                    && req.IncludeUnused),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerPruneRunResult(
                DockerPruneStatus.Success, 4, 4_000_000,
                StartedUtc: completedUtc.AddSeconds(-2), CompletedUtc: completedUtc,
                IncludedUnused: true, Error: null));

        var ctrl = BuildController();
        var result = await ctrl.PruneImages(conn.Id, new PruneImagesRequest(IncludeUnused: true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<PruneImagesResponse>(ok.Value);
        Assert.Equal(DockerPruneStatus.Success, body.Run.Status);
        Assert.Equal(4, body.Run.ImagesDeleted);
        Assert.True(body.Run.IncludedUnused);

        var persisted = await _dbContext.DockerPruneRuns.AsNoTracking().SingleAsync();
        Assert.Equal(conn.Id, persisted.DockerConnectionId);
        Assert.Equal(_userId, persisted.InitiatedByUserId);
        Assert.Equal(DockerPruneTrigger.Manual, persisted.Trigger);
        Assert.Equal(DockerPruneStatus.Success, persisted.Status);

        var refreshed = await _dbContext.DockerConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Equal(completedUtc, refreshed.LastImagePruneUtc);
    }

    [Fact]
    public async Task PruneImages_HostUnreachable_Returns502_AndStillPersistsAuditRow()
    {
        var conn = await SeedConnectionAsync(_userId);
        _pruneRunnerMock
            .Setup(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerPruneRunResult(
                DockerPruneStatus.HostUnreachable, 0, 0,
                StartedUtc: DateTime.UtcNow, CompletedUtc: DateTime.UtcNow,
                IncludedUnused: false, Error: "ssh: connection refused"));

        var ctrl = BuildController();
        var result = await ctrl.PruneImages(conn.Id, new PruneImagesRequest(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);

        var row = await _dbContext.DockerPruneRuns.AsNoTracking().SingleAsync();
        Assert.Equal(DockerPruneStatus.HostUnreachable, row.Status);
        Assert.Equal("ssh: connection refused", row.Error);

        // LastImagePruneUtc must NOT advance when the daemon was unreachable.
        var refreshed = await _dbContext.DockerConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Null(refreshed.LastImagePruneUtc);
    }

    [Fact]
    public async Task PruneImages_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.PruneImages(conn.Id, new PruneImagesRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _pruneRunnerMock.Verify(
            r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // ── V7.9 — container ↔ Proxmox guest cross-link ─────────────────────────

    private async Task<(Guid ConnectionId, int VmId)> SeedProxmoxGuestAsync(Guid ownerUserId, int vmId = 101)
    {
        var pve = new ProxmoxConnectionEntity
        {
            UserId = ownerUserId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ServerType = ProxmoxServerType.Pve,
            ApiTokenId = "root@pam!stash",
        };
        _dbContext.ProxmoxConnections.Add(pve);
        _dbContext.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            ProxmoxConnectionId = pve.Id,
            VmId = vmId,
            GuestType = ProxmoxGuestType.Lxc,
            Name = $"guest-{vmId}",
            IsRunning = true,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return (pve.Id, vmId);
    }

    [Fact]
    public async Task SetProxmoxLink_Upserts_AndListSurfacesChip()
    {
        var conn = await SeedConnectionAsync(_userId);
        var (pveId, vmId) = await SeedProxmoxGuestAsync(_userId);

        var ctrl = BuildController();
        var put = await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, vmId), CancellationToken.None);
        Assert.IsType<OkObjectResult>(put.Result);

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail> { MakeDetail(id: "abc", name: "wp", image: "wordpress:6", state: "running") });

        var list = await ctrl.ListContainers(conn.Id, CancellationToken.None);
        var cards = Assert.IsAssignableFrom<List<DockerContainerCard>>(Assert.IsType<OkObjectResult>(list.Result).Value);
        var card = Assert.Single(cards);
        Assert.NotNull(card.ProxmoxLink);
        Assert.Equal(pveId, card.ProxmoxLink!.ProxmoxConnectionId);
        Assert.Equal(vmId, card.ProxmoxLink.VmId);
        Assert.Equal($"guest-{vmId}", card.ProxmoxLink.GuestName);
    }

    [Fact]
    public async Task SetProxmoxLink_IsUpsert_OnSameContainer()
    {
        var conn = await SeedConnectionAsync(_userId);
        var (pveId, vmId) = await SeedProxmoxGuestAsync(_userId, vmId: 101);
        // A second guest (102) on the SAME host, so re-pointing stays owner-valid.
        _dbContext.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            ProxmoxConnectionId = pveId, VmId = 102, GuestType = ProxmoxGuestType.Lxc, Name = "guest-102", IsRunning = true,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, vmId), CancellationToken.None);
        await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, 102), CancellationToken.None);

        var rows = await _dbContext.ContainerProxmoxLinks.AsNoTracking()
            .Where(l => l.DockerConnectionId == conn.Id && l.ContainerName == "wp").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(102, row.VmId);
    }

    [Fact]
    public async Task SetProxmoxLink_NodeRow_BadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var (pveId, _) = await SeedProxmoxGuestAsync(_userId);

        var ctrl = BuildController();
        var result = await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, 0), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SetProxmoxLink_ForeignGuest_BadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var (pveId, vmId) = await SeedProxmoxGuestAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, vmId), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ListContainers_StaleLink_YieldsNoChip()
    {
        var conn = await SeedConnectionAsync(_userId);
        // A real Proxmox host, but the link points at a vmid the host no longer has
        // (e.g. the guest was destroyed) — must surface no chip rather than erroring.
        var (pveId, _) = await SeedProxmoxGuestAsync(_userId, vmId: 101);
        _dbContext.ContainerProxmoxLinks.Add(new ContainerProxmoxLinkEntity
        {
            UserId = _userId, DockerConnectionId = conn.Id, ContainerName = "wp",
            ProxmoxConnectionId = pveId, VmId = 999,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DockerContainerDetail> { MakeDetail(id: "abc", name: "wp", image: "wordpress:6", state: "running") });

        var ctrl = BuildController();
        var list = await ctrl.ListContainers(conn.Id, CancellationToken.None);
        var cards = Assert.IsAssignableFrom<List<DockerContainerCard>>(Assert.IsType<OkObjectResult>(list.Result).Value);
        Assert.Null(Assert.Single(cards).ProxmoxLink);
    }

    [Fact]
    public async Task RemoveProxmoxLink_ClearsLink()
    {
        var conn = await SeedConnectionAsync(_userId);
        var (pveId, vmId) = await SeedProxmoxGuestAsync(_userId);
        var ctrl = BuildController();
        await ctrl.SetProxmoxLink(conn.Id, "wp", new ContainerProxmoxLinkRequest(pveId, vmId), CancellationToken.None);

        var del = await ctrl.RemoveProxmoxLink(conn.Id, "wp", CancellationToken.None);

        Assert.IsType<NoContentResult>(del);
        Assert.False(await _dbContext.ContainerProxmoxLinks.AnyAsync(l => l.DockerConnectionId == conn.Id));
    }

    private DockerInstancesController BuildController(Guid? userId = null, bool allowRemoval = false)
    {
        var watchMapper = new DockerWatchMapper(_encryptionMock.Object, new ImageReferenceParser());
        var connectionMapper = new DockerConnectionMapper(_encryptionMock.Object);
        var opts = Options.Create(new StashboardOptions { AllowContainerRemoval = allowRemoval });

        var controller = new DockerInstancesController(
            _dbContext, connectionMapper, watchMapper,
            _hostClientMock.Object, _logStreamerMock.Object, _statsStreamerMock.Object,
            _projectUpdaterMock.Object, _selfUpdateLauncherMock.Object, _updateCheckerMock.Object,
            _pruneRunnerMock.Object, _iconResolverMock.Object, opts);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(
        Guid userId, DockerHostType hostType = DockerHostType.LocalSocket)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = hostType,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    private async Task<DockerWatchEntity> SeedWatchAsync(
        Guid webResourceId, Guid userId, Guid connectionId, string containerName)
    {
        // Wire the connection assignment so the per-watch endpoints stay
        // in sync with the page's view (not strictly needed for these
        // tests but mirrors how the app stitches them together).
        await _dbContext.WebResources
            .Where(s => s.Id == webResourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DockerConnectionId, connectionId));

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connectionId,
            WebResourceId = webResourceId,
            UserId = userId,
            Label = "app",
            Enabled = true,
            ImageReference = "ghcr.io/owner/repo:v1",
            RegistryHost = "ghcr.io",
            Repository = "owner/repo",
            Tag = "v1",
            ContainerName = containerName,
            UpdateStatus = DockerUpdateStatus.Unknown,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return watch;
    }

    private static DockerContainerDetail MakeDetail(
        string id, string name, string image, string state,
        string? composeProject = null, string? composeService = null,
        IReadOnlyDictionary<string, string>? labels = null) =>
        new(
            Id: id,
            Name: name,
            Image: image,
            ImageId: $"sha256:{id}",
            State: state,
            Status: $"Up 1 hour ({state})",
            CreatedUtc: DateTime.UtcNow.AddHours(-1),
            Ports: Array.Empty<DockerContainerPort>(),
            ComposeProject: composeProject,
            ComposeService: composeService,
            Labels: labels ?? new Dictionary<string, string>());

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await _dbContext.Database.EnsureDeletedAsync();
                await _dbContext.Database.EnsureCreatedAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
    }

    private async Task ClearAllDataAsync()
    {
        await _dbContext.DockerPruneRuns.ExecuteDeleteAsync();
        await _dbContext.ContainerIcons.ExecuteDeleteAsync();
        await _dbContext.ContainerProxmoxLinks.ExecuteDeleteAsync();
        await _dbContext.ProxmoxGuests.ExecuteDeleteAsync();
        await _dbContext.ProxmoxConnections.ExecuteDeleteAsync();
        await _dbContext.DockerUpdateAttempts.ExecuteDeleteAsync();
        await _dbContext.DockerWatches.ExecuteDeleteAsync();
        await _dbContext.DockerConnections.ExecuteDeleteAsync();
        await _dbContext.WebResourceTags.ExecuteDeleteAsync();
        await _dbContext.Credentials.ExecuteDeleteAsync();
        await _dbContext.WebResources.ExecuteDeleteAsync();
        await _dbContext.Tags.ExecuteDeleteAsync();
        await _dbContext.Categories.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString()
    {
        var apiDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Stashboard.Api"));
        var config = new ConfigurationBuilder()
            .SetBasePath(apiDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables(prefix: "STASHBOARD_")
            .Build();
        var devCs = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        _ = devCs; // dev connection string unused; tests use a local SQLite file
        return $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests.db")};Pooling=False";
    }
}


