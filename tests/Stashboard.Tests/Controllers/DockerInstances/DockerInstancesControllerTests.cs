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
    }

    public async Task InitializeAsync()
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

    public Task DisposeAsync() => _dbContext.DisposeAsync().AsTask();

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
        var result = await ctrl.Remove(conn.Id, "wp", force: false, CancellationToken.None);

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
        var result = await ctrl.Remove(conn.Id, "db", force: false, CancellationToken.None);

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
        var result = await ctrl.Remove(conn.Id, "app", force: false, CancellationToken.None);

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
        var result = await ctrl.Remove(conn.Id, "redis", force: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerActionResponse>(ok.Value);
        Assert.Equal(DockerContainerActionType.Remove, response.Attempt.ActionType);
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

    // ── helpers ──────────────────────────────────────────────────────────────

    private DockerInstancesController BuildController(Guid? userId = null, bool allowRemoval = false)
    {
        var watchMapper = new DockerWatchMapper(_encryptionMock.Object, new ImageReferenceParser());
        var connectionMapper = new DockerConnectionMapper(_encryptionMock.Object);
        var opts = Options.Create(new StashboardOptions { AllowContainerRemoval = allowRemoval });

        var controller = new DockerInstancesController(
            _dbContext, connectionMapper, watchMapper,
            _hostClientMock.Object, _logStreamerMock.Object, _statsStreamerMock.Object, opts);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(Guid userId)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = DockerHostType.LocalSocket,
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

    private static DockerContainerDetail MakeDetail(string id, string name, string image, string state) =>
        new(
            Id: id,
            Name: name,
            Image: image,
            ImageId: $"sha256:{id}",
            State: state,
            Status: $"Up 1 hour ({state})",
            CreatedUtc: DateTime.UtcNow.AddHours(-1),
            Ports: Array.Empty<DockerContainerPort>(),
            ComposeProject: null,
            ComposeService: null,
            Labels: new Dictionary<string, string>());

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
