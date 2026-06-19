using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.ComposeProjects;

/// <summary>
/// V7.0/V7.1 — endpoint tests for <see cref="ComposeProjectsController"/>:
/// label-based project discovery (incl. the LocalSocket path mapping), the
/// owner-scoping check, the error envelopes (404 / 400 / 422 / 409 / 502),
/// the happy-path viewer response, and the V7.1 edit flow (diff → validate →
/// atomic save, with the read-only banner blocking writes).
/// </summary>
public class ComposeProjectsControllerTests : IAsyncLifetime
{
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly Mock<IComposeProjectReader> _readerMock = new();
    private readonly Mock<IComposeProjectWriter> _writerMock = new();
    private readonly Mock<IComposeHistoryStore> _historyMock = new();
    private readonly Mock<IComposeCommandRunner> _composeRunnerMock = new();
    private readonly Mock<IDockerHostClient> _hostClientMock = new();
    private readonly Mock<IRegistryClient> _registryMock = new();
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ComposeProjectsControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        // The SSH branch decrypts the connection's private key through the
        // mapper, so the mock has to round-trip the seeded "enc:" values.
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

    // ── discovery (GET /compose) ─────────────────────────────────────────────

    [Fact]
    public async Task ListProjects_Returns404_WhenConnectionBelongsToAnotherUser()
    {
        var conn = await SeedConnectionAsync(_otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.ListProjects(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.ListContainerDetailsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListProjects_GroupsContainersByProjectLabel_AndSkipsStandaloneContainers()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(
            Container("jellyfin", project: "media", workingDir: "/opt/stacks/media", state: "running"),
            Container("media-db", project: "media", workingDir: "/opt/stacks/media", state: "exited"),
            Container("immich", project: "immich", workingDir: "/opt/stacks/immich", state: "running"),
            Container("portainer", project: null, workingDir: null, state: "running"));

        var ctrl = BuildController();
        var result = await ctrl.ListProjects(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsAssignableFrom<IReadOnlyList<ComposeDiscoveredProjectResponse>>(ok.Value);
        Assert.Equal(2, body.Count);

        var immich = body[0];
        Assert.Equal("immich", immich.Name);
        Assert.Equal("/opt/stacks/immich", immich.WorkingDir);
        Assert.Equal("/opt/stacks/immich", immich.ResolvedPath); // no mapping → as-is
        Assert.Equal(1, immich.ContainerCount);
        Assert.Equal(1, immich.RunningCount);

        var media = body[1];
        Assert.Equal("media", media.Name);
        Assert.Equal(2, media.ContainerCount);
        Assert.Equal(1, media.RunningCount);
    }

    [Fact]
    public async Task GetAllocation_ReturnsReservedCapacity_AndCachesTheInspectSweep()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.GetResourceAllocationAsync(
                It.IsAny<DockerHostTransport>(), "media", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerResourceAllocation(ContainerCount: 3, ReservedCpus: 4.5, ReservedMemoryBytes: 8L * 1024 * 1024 * 1024));

        var ctrl = BuildController();
        var first = await ctrl.GetAllocation(conn.Id, "media", CancellationToken.None);
        var second = await ctrl.GetAllocation(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(first.Result);
        var body = Assert.IsType<ComposeAllocationResponse>(ok.Value);
        Assert.Equal(3, body.ContainerCount);
        Assert.Equal(4.5, body.ReservedCpus);
        Assert.Equal(8L * 1024 * 1024 * 1024, body.ReservedMemoryBytes);
        Assert.IsType<OkObjectResult>(second.Result);

        // Second call within the TTL is served from cache — only one inspect sweep.
        _hostClientMock.Verify(h => h.GetResourceAllocationAsync(
            It.IsAny<DockerHostTransport>(), "media", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListProjects_LocalSocketMapping_TranslatesHostPrefix()
    {
        var conn = await SeedConnectionAsync(_userId, hostPrefix: "/opt/stacks", containerPrefix: "/compose");
        SetupContainers(Container("jellyfin", project: "media", workingDir: "/opt/stacks/media"));

        var ctrl = BuildController();
        var result = await ctrl.ListProjects(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsAssignableFrom<IReadOnlyList<ComposeDiscoveredProjectResponse>>(ok.Value);
        var media = Assert.Single(body);
        Assert.Equal("/opt/stacks/media", media.WorkingDir);
        Assert.Equal("/compose/media", media.ResolvedPath);
    }

    [Fact]
    public async Task ListProjects_HostUnreachable_Returns502()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var ctrl = BuildController();
        var result = await ctrl.ListProjects(conn.Id, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    // ── viewer (GET /compose/{project}) ──────────────────────────────────────

    [Fact]
    public async Task GetProject_Returns404_WhenProjectIsNotOnTheHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("jellyfin", project: "media", workingDir: "/opt/stacks/media"));

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "ghost", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        _readerMock.Verify(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProject_Returns404_WhenProjectHasNoWorkingDirLabel()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("jellyfin", project: "media", workingDir: null));

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "media", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains("working_dir", notFound.Value!.ToString());
    }

    [Fact]
    public async Task GetProject_Returns400_ForTcpTlsConnections()
    {
        var conn = await SeedConnectionAsync(_userId, hostType: DockerHostType.TcpTls);

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "media", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _hostClientMock.Verify(h => h.ListContainerDetailsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProject_ReadsTheResolvedPath_AndReturnsParsedProject()
    {
        var conn = await SeedConnectionAsync(_userId, hostPrefix: "/opt/stacks", containerPrefix: "/compose");
        SetupContainers(Container("jellyfin", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/compose/media", "docker-compose.yml", """
            services:
              web:
                image: nginx:1.27
                restart: unless-stopped
                ports:
                  - "8080:80"
                labels:
                  com.example.team: media
                command: nginx -g "daemon off;"
                user: "1000:1000"
                working_dir: /srv
              db:
                image: mariadb:11
            volumes:
              data:
            """);

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeProjectResponse>(ok.Value);
        Assert.Equal("docker-compose.yml", body.FileName);
        Assert.Equal("/compose/media", body.ProjectPath);
        Assert.Equal(2, body.Services.Count);
        var web = body.Services[0];
        Assert.Equal("nginx:1.27", web.Image);
        Assert.Equal(new[] { "8080:80" }, web.Ports);
        // V7.1 — the new editable fields flow through the response.
        Assert.Equal("media", Assert.Single(web.Labels).Value);
        Assert.Equal("nginx -g \"daemon off;\"", web.Command);
        Assert.Equal("1000:1000", web.User);
        Assert.Equal("/srv", web.WorkingDir);
        Assert.Equal(new[] { "data" }, body.Volumes.Select(v => v.Name));
        Assert.Empty(body.UnsupportedFeatures);
    }

    [Fact]
    public async Task GetProject_Returns422_WhenYamlIsUnparseable()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("jellyfin", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "compose.yaml", "not: [valid");

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "media", CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetProject_SshConnection_ReadsTheFileOverSsh()
    {
        var conn = await SeedSshConnectionAsync(_userId);
        SetupContainers(Container("web", project: "stack", workingDir: "/srv/stack"));
        _readerMock
            .Setup(r => r.ReadOverSshAsync(
                It.Is<DockerSshCredentials>(s => s.Host == "vps.example.com" && s.Username == "docker"),
                "/srv/stack",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(
                ComposeProjectReadStatus.Ok, "docker-compose.yml", "services:\n  web:\n    image: nginx:1.27\n", null));

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "stack", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeProjectResponse>(ok.Value);
        Assert.Equal("nginx:1.27", body.Services.Single().Image);
        // The local filesystem path must never be probed for an SSH host.
        _readerMock.Verify(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetProject_SshReadFailure_Returns502()
    {
        var conn = await SeedSshConnectionAsync(_userId);
        SetupContainers(Container("web", project: "stack", workingDir: "/srv/stack"));
        _readerMock
            .Setup(r => r.ReadOverSshAsync(
                It.IsAny<DockerSshCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(
                ComposeProjectReadStatus.SshFailed, null, null, "SSH to vps.example.com:22 failed: connection refused"));

        var ctrl = BuildController();
        var result = await ctrl.GetProject(conn.Id, "stack", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    // ── V7.1 — edit (PUT /compose/{project}/services/{service}) ─────────────

    private const string EditableYaml = """
        services:
          web:
            image: nginx:1.27
            restart: unless-stopped
            ports:
              - "8080:80"
          db:
            image: mariadb:11
        """;

    [Fact]
    public async Task EditService_DiffsValidatesAndSavesAtomically()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var request = EditRequest(image: "nginx:1.28", ports: new[] { "8080:80" });
        var result = await ctrl.EditService(conn.Id, "media", "web", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeServiceEditResponse>(ok.Value);
        Assert.True(body.Changed);
        Assert.Equal("nginx:1.28", body.Project.Services.Single(s => s.Name == "web").Image);

        // The saved content is the surgical edit: image swapped, db untouched.
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/media", "docker-compose.yml",
            It.Is<string>(content => content.Contains("image: nginx:1.28") && content.Contains("image: mariadb:11")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditService_NoChanges_SkipsTheWriteEntirely()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);

        var ctrl = BuildController();
        var request = EditRequest(image: "nginx:1.27", restart: "unless-stopped", ports: new[] { "8080:80" });
        var result = await ctrl.EditService(conn.Id, "media", "web", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeServiceEditResponse>(ok.Value);
        Assert.False(body.Changed);
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditService_FileWithUnsupportedFeatures_Returns409WithoutWriting()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "compose.yaml", """
            x-common: &common
              restart: always
            services:
              web:
                <<: *common
                image: nginx:1.27
            """);

        var ctrl = BuildController();
        var result = await ctrl.EditService(conn.Id, "media", "web", EditRequest(image: "nginx:1.28"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("read-only", conflict.Value!.ToString());
        _writerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EditService_UnknownService_Returns422()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);

        var ctrl = BuildController();
        var result = await ctrl.EditService(conn.Id, "media", "ghost", EditRequest(image: "x:1"), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        Assert.Contains("'ghost' was not found", unprocessable.Value!.ToString());
    }

    [Fact]
    public async Task EditService_ValidationFailure_Returns422WithComposeStderr()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeWriteResult(ComposeWriteStatus.ValidationFailed,
                "services.web.ports contains an invalid type"));

        var ctrl = BuildController();
        var request = EditRequest(image: "nginx:1.28", ports: new[] { "8080:80" });
        var result = await ctrl.EditService(conn.Id, "media", "web", request, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        Assert.Contains("invalid type", unprocessable.Value!.ToString());
    }

    [Fact]
    public async Task EditService_SshConnection_SavesOverSsh()
    {
        var conn = await SeedSshConnectionAsync(_userId);
        SetupContainers(Container("web", project: "stack", workingDir: "/srv/stack"));
        _readerMock
            .Setup(r => r.ReadOverSshAsync(It.IsAny<DockerSshCredentials>(), "/srv/stack", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(
                ComposeProjectReadStatus.Ok, "docker-compose.yml", EditableYaml, null));
        _writerMock
            .Setup(w => w.WriteOverSshAsync(
                It.Is<DockerSshCredentials>(s => s.Host == "vps.example.com"),
                "/srv/stack", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var request = EditRequest(image: "nginx:1.28", ports: new[] { "8080:80" });
        var result = await ctrl.EditService(conn.Id, "stack", "web", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ComposeServiceEditResponse>(ok.Value).Changed);
        _writerMock.Verify(w => w.WriteOverSshAsync(
            It.IsAny<DockerSshCredentials>(), "/srv/stack", "docker-compose.yml",
            It.Is<string>(c => c.Contains("image: nginx:1.28")), It.IsAny<CancellationToken>()), Times.Once);
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V7.4 — create service (POST /compose/{project}/services) ────────────

    private static ComposeServiceCreateRequest CreateRequest(
        string name, string? image = "redis:7",
        IReadOnlyList<string>? ports = null) =>
        new(
            Name: name,
            Image: image,
            Restart: "unless-stopped",
            Ports: ports ?? Array.Empty<string>(),
            Volumes: Array.Empty<string>(),
            Environment: Array.Empty<ComposeEnvVarResponse>(),
            Labels: Array.Empty<ComposeEnvVarResponse>(),
            Command: null, Entrypoint: null, User: null, WorkingDir: null);

    [Fact]
    public async Task CreateService_AppendsValidatesAndSavesAtomically()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var result = await ctrl.CreateService(conn.Id, "media", CreateRequest("cache", ports: new[] { "6379:6379" }), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeServiceEditResponse>(ok.Value);
        Assert.True(body.Changed);
        // The new service is present alongside the originals.
        Assert.Contains(body.Project.Services, s => s.Name == "cache" && s.Image == "redis:7");
        Assert.Contains(body.Project.Services, s => s.Name == "web");
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/media", "docker-compose.yml",
            It.Is<string>(c => c.Contains("cache:") && c.Contains("image: redis:7") && c.Contains("image: nginx:1.27")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateService_DuplicateName_Returns422WithoutWriting()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);

        var ctrl = BuildController();
        var result = await ctrl.CreateService(conn.Id, "media", CreateRequest("web"), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        Assert.Contains("already exists", unprocessable.Value!.ToString());
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateService_FileWithUnsupportedFeatures_Returns409()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "compose.yaml", """
            x-common: &common
              restart: always
            services:
              web:
                <<: *common
                image: nginx:1.27
            """);

        var ctrl = BuildController();
        var result = await ctrl.CreateService(conn.Id, "media", CreateRequest("cache"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _writerMock.VerifyNoOtherCalls();
    }

    // ── V7.4 — raw file editor (GET/PUT /compose/{project}/file) ────────────

    [Fact]
    public async Task GetFile_ReturnsRawContent()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);

        var ctrl = BuildController();
        var result = await ctrl.GetFile(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeFileResponse>(ok.Value);
        Assert.Equal("docker-compose.yml", body.FileName);
        Assert.Equal(EditableYaml, body.Content);
    }

    [Fact]
    public async Task SaveFile_ChangedContent_ValidatesAndWrites()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var newContent = EditableYaml + "\n  extra:\n    image: alpine:3\n";
        var result = await ctrl.SaveFile(conn.Id, "media", new ComposeFileSaveRequest(newContent), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ComposeFileSaveResponse>(ok.Value).Changed);
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/media", "docker-compose.yml", newContent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveFile_IdenticalContent_SkipsTheWrite()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);

        var ctrl = BuildController();
        var result = await ctrl.SaveFile(conn.Id, "media", new ComposeFileSaveRequest(EditableYaml), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(Assert.IsType<ComposeFileSaveResponse>(ok.Value).Changed);
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveFile_ValidationFailure_Returns422WithComposeStderr()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeWriteResult(ComposeWriteStatus.ValidationFailed, "yaml: line 9: did not find expected key"));

        var ctrl = BuildController();
        var result = await ctrl.SaveFile(conn.Id, "media", new ComposeFileSaveRequest("services:\n  bad: ["), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        Assert.Contains("did not find expected key", unprocessable.Value!.ToString());
    }

    // ── V7.6 — diff / dry-run / apply / history ──────────────────────────────

    [Fact]
    public async Task DiffFile_ReturnsDiffValidationAndChangedServices()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.ValidateAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var newContent = EditableYaml.Replace("nginx:1.27", "nginx:1.28");
        var ctrl = BuildController();
        var result = await ctrl.DiffFile(conn.Id, "media", new ComposeFileDiffRequest(newContent), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeFileDiffResponse>(ok.Value);
        Assert.True(body.Valid);
        Assert.True(body.CliAvailable);
        Assert.False(body.Unchanged);
        Assert.Equal(new[] { "web" }, body.ChangedServices);   // only web's image moved
        Assert.Empty(body.RemovedServices);
        Assert.Contains(body.Diff, l => l.Type == ComposeDiffLineType.Added && l.Text.Contains("nginx:1.28"));
        Assert.Contains(body.Diff, l => l.Type == ComposeDiffLineType.Removed && l.Text.Contains("nginx:1.27"));
    }

    [Fact]
    public async Task DiffFile_InvalidProposedContent_ReportsValidationError()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.ValidateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeWriteResult(ComposeWriteStatus.ValidationFailed, "yaml: line 3: bad indent"));

        var ctrl = BuildController();
        var result = await ctrl.DiffFile(conn.Id, "media", new ComposeFileDiffRequest("services:\n  bad: ["), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeFileDiffResponse>(ok.Value);
        Assert.False(body.Valid);
        Assert.Contains("bad indent", body.ValidationError);
    }

    [Fact]
    public async Task SaveFile_ChangedContent_WritesComposeChangeAuditRow()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var newContent = EditableYaml.Replace("nginx:1.27", "nginx:1.28");
        await ctrl.SaveFile(conn.Id, "media", new ComposeFileSaveRequest(newContent), CancellationToken.None);

        var row = await _dbContext.ComposeChangeAudits.AsNoTracking().SingleAsync(a => a.DockerConnectionId == conn.Id);
        Assert.Equal(ComposeChangeType.Save, row.ChangeType);
        Assert.Equal("media", row.ComposeProject);
        Assert.Equal("web", row.ChangedServices);
        Assert.True(row.Success);
        Assert.Equal(_userId, row.InitiatedByUserId);
    }

    [Fact]
    public async Task ApplyProject_RecreatesServices_AndWritesApplyAudit()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(
                It.Is<ComposeUpRequest>(req => req.ProjectPath == "/opt/stacks/media" && req.Services != null && req.Services.Contains("web")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "recreated", null));

        var ctrl = BuildController();
        var result = await ctrl.ApplyProject(conn.Id, "media", new ComposeApplyRequest(new[] { "web" }), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeApplyResponse>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(new[] { "web" }, body.AppliedServices);

        var row = await _dbContext.ComposeChangeAudits.AsNoTracking().SingleAsync(a => a.DockerConnectionId == conn.Id);
        Assert.Equal(ComposeChangeType.Apply, row.ChangeType);
        Assert.Equal("web", row.ChangedServices);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task ApplyProject_Failure_Returns502_AndAuditsFailure()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(It.IsAny<ComposeUpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.CommandFailed, 1, "", "port is already allocated"));

        var ctrl = BuildController();
        var result = await ctrl.ApplyProject(conn.Id, "media", new ComposeApplyRequest(new[] { "web" }), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, status.StatusCode);
        Assert.Contains("already allocated", Assert.IsType<ComposeApplyResponse>(status.Value).Error);

        var row = await _dbContext.ComposeChangeAudits.AsNoTracking().SingleAsync(a => a.DockerConnectionId == conn.Id);
        Assert.Equal(ComposeChangeType.Apply, row.ChangeType);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task GetHistory_ReturnsKeptRevisions()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _historyMock
            .Setup(h => h.ListAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeHistoryListResult.Ok(new[]
            {
                new ComposeHistoryEntry("20260102T120000000__docker-compose.yml", DateTime.UtcNow, 256),
                new ComposeHistoryEntry("20260101T120000000__docker-compose.yml", DateTime.UtcNow.AddDays(-1), 128),
            }));

        var ctrl = BuildController();
        var result = await ctrl.GetHistory(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsAssignableFrom<IReadOnlyList<ComposeHistoryEntryResponse>>(ok.Value);
        Assert.Equal(2, body.Count);
        Assert.Equal("20260102T120000000__docker-compose.yml", body[0].Id);
        Assert.Equal(256, body[0].SizeBytes);
    }

    [Fact]
    public async Task RestoreHistory_WritesRevisionAndAuditsRestore()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        var revision = EditableYaml.Replace("nginx:1.27", "nginx:1.20");
        _historyMock
            .Setup(h => h.ReadAsync("/opt/stacks/media", "rev1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeHistoryReadResult(ComposeHistoryStatus.Ok, revision, null));
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", revision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var result = await ctrl.RestoreHistory(conn.Id, "media", "rev1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeRestoreResponse>(ok.Value);
        Assert.True(body.Changed);
        Assert.Equal("nginx:1.20", body.Project.Services.Single(s => s.Name == "web").Image);

        var row = await _dbContext.ComposeChangeAudits.AsNoTracking().SingleAsync(a => a.DockerConnectionId == conn.Id);
        Assert.Equal(ComposeChangeType.Restore, row.ChangeType);
        Assert.Equal("web", row.ChangedServices);
    }

    [Fact]
    public async Task RestoreHistory_MissingRevision_Returns404()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _historyMock
            .Setup(h => h.ReadAsync("/opt/stacks/media", "gone", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeHistoryReadResult(ComposeHistoryStatus.NotFound, null, "not found"));

        var ctrl = BuildController();
        var result = await ctrl.RestoreHistory(conn.Id, "media", "gone", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ── V7.4 — bring the project up (POST /compose/{project}/up) ─────────────

    [Fact]
    public async Task UpProject_Local_RunsComposeUp()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(
                It.Is<ComposeUpRequest>(req => req.ProjectPath == "/opt/stacks/media" && req.Ssh == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "started", null));

        var ctrl = BuildController();
        var result = await ctrl.UpProject(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ComposeUpResponse>(ok.Value).Success);
    }

    [Fact]
    public async Task UpProject_Ssh_PassesSshCredentials()
    {
        var conn = await SeedSshConnectionAsync(_userId);
        SetupContainers(Container("web", project: "stack", workingDir: "/srv/stack"));
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(
                It.Is<ComposeUpRequest>(req => req.ProjectPath == "/srv/stack" && req.Ssh != null && req.Ssh.Host == "vps.example.com"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "started", null));

        var ctrl = BuildController();
        var result = await ctrl.UpProject(conn.Id, "stack", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpProject_Failure_Returns502WithError()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(It.IsAny<ComposeUpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.CommandFailed, 1, "", "port is already allocated"));

        var ctrl = BuildController();
        var result = await ctrl.UpProject(conn.Id, "media", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, status.StatusCode);
        Assert.Contains("already allocated", Assert.IsType<ComposeUpResponse>(status.Value).Error);
    }

    // ── V7.4.1 — create project from scratch (POST /compose/create-project) ───

    private static ComposeProjectCreateRequest NewProjectRequest(
        string projectName = "myapp", string directory = "/opt/stacks/myapp",
        bool createDirectory = true, bool run = false, string serviceName = "web",
        string? image = "nginx:1.27") =>
        new(
            ProjectName: projectName,
            Directory: directory,
            FileName: null,
            CreateDirectory: createDirectory,
            Run: run,
            Services: [CreateRequest(serviceName, image)]);

    private void SetupReadMissing(string path) =>
        _readerMock
            .Setup(r => r.ReadAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(ComposeProjectReadStatus.DirectoryNotFound, null, null,
                "Directory not found."));

    [Fact]
    public async Task CreateProject_WritesNewFileAndRuns()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupReadMissing("/opt/stacks/myapp");
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/myapp", "docker-compose.yml", It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(
                It.Is<ComposeUpRequest>(req => req.ProjectPath == "/opt/stacks/myapp" && req.Ssh == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "started", null));

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(run: true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeProjectCreateResponse>(ok.Value);
        Assert.Equal("myapp", body.ProjectName);
        Assert.True(body.Started);
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/myapp", "docker-compose.yml",
            It.Is<string>(c => c.Contains("name: myapp") && c.Contains("image: nginx:1.27")),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateProject_MultipleServices_WritesAllIntoOneFile()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupReadMissing("/opt/stacks/myapp");
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/myapp", "docker-compose.yml", It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var request = new ComposeProjectCreateRequest(
            ProjectName: "myapp",
            Directory: "/opt/stacks/myapp",
            FileName: null,
            CreateDirectory: true,
            Run: false,
            Services: [CreateRequest("app", "wordpress:6"), CreateRequest("db", "mariadb:11")]);

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("myapp", Assert.IsType<ComposeProjectCreateResponse>(ok.Value).ProjectName);
        // Both services land in the single file the first one seeded.
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/myapp", "docker-compose.yml",
            It.Is<string>(c => c.Contains("name: myapp")
                && c.Contains("app:") && c.Contains("image: wordpress:6")
                && c.Contains("db:") && c.Contains("image: mariadb:11")),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateProject_CreateOnly_DoesNotRun()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupReadMissing("/opt/stacks/myapp");
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(run: false), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(Assert.IsType<ComposeProjectCreateResponse>(ok.Value).Started);
        _composeRunnerMock.Verify(r => r.UpProjectAsync(It.IsAny<ComposeUpRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateProject_ExistingFile_Returns409WithoutWriting()
    {
        var conn = await SeedConnectionAsync(_userId);
        _readerMock
            .Setup(r => r.ReadAsync("/opt/stacks/myapp", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(ComposeProjectReadStatus.Ok, "docker-compose.yml", "services: {}\n", null));

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("already exists", conflict.Value!.ToString());
        _writerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateProject_DirectoryMissingAndNoCreate_Returns400()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupReadMissing("/opt/stacks/myapp");

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(createDirectory: false), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _writerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateProject_InvalidProjectName_Returns422()
    {
        var conn = await SeedConnectionAsync(_userId);

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(projectName: "MyApp"), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        Assert.Contains("must match", unprocessable.Value!.ToString());
        _readerMock.Verify(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateProject_Ssh_WritesAndRunsOverSsh()
    {
        var conn = await SeedSshConnectionAsync(_userId);
        _readerMock
            .Setup(r => r.ReadOverSshAsync(It.IsAny<DockerSshCredentials>(), "/srv/new", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(ComposeProjectReadStatus.DirectoryNotFound, null, null, "missing"));
        _writerMock
            .Setup(w => w.WriteOverSshAsync(
                It.IsAny<DockerSshCredentials>(), "/srv/new", "docker-compose.yml", It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);
        _composeRunnerMock
            .Setup(r => r.UpProjectAsync(
                It.Is<ComposeUpRequest>(req => req.ProjectPath == "/srv/new" && req.Ssh != null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "started", null));

        var ctrl = BuildController();
        var result = await ctrl.CreateProject(conn.Id, NewProjectRequest(directory: "/srv/new", run: true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<ComposeProjectCreateResponse>(ok.Value).Started);
    }

    // ── V7.3 — top-level resource edit / delete ─────────────────────────────

    private static ComposeResourceEditRequest ResourceRequest(
        bool external = false, string? nameOverride = null, string? driver = null,
        string? subnet = null, string? gateway = null, string? file = null) =>
        new(external, nameOverride, driver, subnet, gateway, file, DriverOpts: null);

    [Fact]
    public async Task EditResource_AddsNetwork_ValidatesAndSavesAtomically()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", EditableYaml);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var request = ResourceRequest(driver: "bridge", subnet: "172.30.0.0/24");
        var result = await ctrl.EditResource(conn.Id, "media", "networks", "backend", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeResourceEditResponse>(ok.Value);
        Assert.True(body.Changed);
        var net = body.Project.Networks.Single(n => n.Name == "backend");
        Assert.Equal("bridge", net.Driver);
        Assert.Equal("172.30.0.0/24", net.Subnet);
        _writerMock.Verify(w => w.WriteAsync(
            "/opt/stacks/media", "docker-compose.yml",
            It.Is<string>(c => c.Contains("backend:") && c.Contains("172.30.0.0/24")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditResource_UnknownKind_Returns400()
    {
        var conn = await SeedConnectionAsync(_userId);

        var ctrl = BuildController();
        var result = await ctrl.EditResource(
            conn.Id, "media", "gremlins", "x", ResourceRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _readerMock.Verify(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditResource_FileWithUnsupportedFeatures_Returns409WithoutWriting()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "compose.yaml", """
            x-common: &common
              restart: always
            services:
              web:
                <<: *common
                image: nginx:1.27
            """);

        var ctrl = BuildController();
        var result = await ctrl.EditResource(
            conn.Id, "media", "volumes", "data", ResourceRequest(driver: "local"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _writerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteResource_RemovesEntry_AndSaves()
    {
        var conn = await SeedConnectionAsync(_userId);
        SetupContainers(Container("web", project: "media", workingDir: "/opt/stacks/media"));
        SetupRead("/opt/stacks/media", "docker-compose.yml", """
            services:
              web:
                image: nginx:1.27
            networks:
              frontend:
                driver: bridge
              backend:
                driver: bridge
            """);
        _writerMock
            .Setup(w => w.WriteAsync("/opt/stacks/media", "docker-compose.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComposeWriteResult.Ok);

        var ctrl = BuildController();
        var result = await ctrl.DeleteResource(conn.Id, "media", "networks", "frontend", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeResourceEditResponse>(ok.Value);
        Assert.True(body.Changed);
        Assert.DoesNotContain(body.Project.Networks, n => n.Name == "frontend");
        Assert.Contains(body.Project.Networks, n => n.Name == "backend");
    }

    [Fact]
    public async Task GetHostNetworks_ReturnsHostSubnets()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.ListNetworksAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new DockerNetworkSummary("br-lan", "bridge", new[] { "172.20.0.0/16" }) });

        var ctrl = BuildController();
        var result = await ctrl.GetHostNetworks(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsAssignableFrom<IReadOnlyList<ComposeHostNetworkResponse>>(ok.Value);
        Assert.Equal("br-lan", Assert.Single(body).Name);
        Assert.Equal("172.20.0.0/16", body[0].Subnets.Single());
    }

    [Fact]
    public async Task GetVolumeUsage_ReturnsSizes()
    {
        var conn = await SeedConnectionAsync(_userId);
        _hostClientMock
            .Setup(h => h.GetVolumeUsageAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new DockerVolumeUsage("media_data", 4_509_715_660, 1) });

        var ctrl = BuildController();
        var result = await ctrl.GetVolumeUsage(conn.Id, "media", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsAssignableFrom<IReadOnlyList<ComposeVolumeUsageResponse>>(ok.Value);
        Assert.Equal(4_509_715_660, Assert.Single(body).SizeBytes);
    }

    // ── V7.1 — image tags (GET /compose/image-tags) ─────────────────────────

    [Fact]
    public async Task GetImageTags_ParsesTheReference_AndReturnsRegistryTags()
    {
        var conn = await SeedConnectionAsync(_userId);
        _registryMock
            .Setup(r => r.ListTagsAsync("docker.io", "library/nginx", It.IsAny<RegistryAuthContext>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(
                RegistryManifestStatus.Ok, new[] { "1.27", "1.28", "latest" }, null, DateTime.UtcNow));

        var ctrl = BuildController();
        var result = await ctrl.GetImageTags(conn.Id, "nginx:1.27", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeImageTagsResponse>(ok.Value);
        Assert.Equal("library/nginx", body.Repository);
        Assert.Equal(new[] { "1.27", "1.28", "latest" }, body.Tags);
        Assert.Null(body.Error);
    }

    [Fact]
    public async Task GetImageTags_RegistryFailure_SurfacesErrorWithEmptyList()
    {
        var conn = await SeedConnectionAsync(_userId);
        _registryMock
            .Setup(r => r.ListTagsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RegistryAuthContext>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(
                RegistryManifestStatus.Unauthorized, Array.Empty<string>(), "401 from registry", DateTime.UtcNow));

        var ctrl = BuildController();
        var result = await ctrl.GetImageTags(conn.Id, "ghcr.io/owner/private:1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ComposeImageTagsResponse>(ok.Value);
        Assert.Empty(body.Tags);
        Assert.Contains("401", body.Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ComposeServiceEditRequest EditRequest(
        string? image = null,
        string? restart = "unless-stopped",
        IReadOnlyList<string>? ports = null,
        IReadOnlyList<string>? volumes = null) =>
        new(
            Image: image,
            Restart: restart,
            Ports: ports ?? Array.Empty<string>(),
            Volumes: volumes ?? Array.Empty<string>(),
            Environment: Array.Empty<ComposeEnvVarResponse>(),
            Labels: Array.Empty<ComposeEnvVarResponse>(),
            Command: null,
            Entrypoint: null,
            User: null,
            WorkingDir: null);

    private static DockerContainerDetail Container(
        string name, string? project, string? workingDir, string state = "running")
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (project is not null) labels["com.docker.compose.project"] = project;
        if (workingDir is not null) labels[WorkingDirLabel] = workingDir;
        return new DockerContainerDetail(
            Id: $"id-{name}", Name: name, Image: "img:1", ImageId: "sha256:x",
            State: state, Status: state, CreatedUtc: DateTime.UtcNow,
            Ports: Array.Empty<DockerContainerPort>(),
            ComposeProject: project, ComposeService: project is null ? null : name,
            Labels: labels);
    }

    private void SetupContainers(params DockerContainerDetail[] containers) =>
        _hostClientMock
            .Setup(h => h.ListContainerDetailsAsync(It.IsAny<DockerHostTransport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(containers);

    private void SetupRead(string path, string fileName, string content) =>
        _readerMock
            .Setup(r => r.ReadAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeProjectReadResult(ComposeProjectReadStatus.Ok, fileName, content, null));

    private ComposeProjectsController BuildController(Guid? userId = null)
    {
        var parser = new ComposeFileParser();
        var controller = new ComposeProjectsController(
            _dbContext,
            new DockerConnectionMapper(_encryptionMock.Object),
            _hostClientMock.Object,
            _readerMock.Object,
            parser,
            new ComposeFileEditor(parser),
            _writerMock.Object,
            _historyMock.Object,
            _composeRunnerMock.Object,
            new ImageReferenceParser(),
            _registryMock.Object,
            new MemoryCache(new MemoryCacheOptions()));
        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(
        Guid userId,
        DockerHostType hostType = DockerHostType.LocalSocket,
        string? hostPrefix = null,
        string? containerPrefix = null)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = hostType,
            HostUrl = hostType == DockerHostType.TcpTls ? "tcp://h:2376" : null,
            ComposePathHostPrefix = hostPrefix,
            ComposePathContainerPrefix = containerPrefix,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    private async Task<DockerConnectionEntity> SeedSshConnectionAsync(Guid userId, bool withPrivateKey = true)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = DockerHostType.Ssh,
            SshHost = "vps.example.com",
            SshPort = 22,
            SshUsername = "docker",
            SshPrivateKeyEncrypted = withPrivateKey ? "enc:PEM" : null,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

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
