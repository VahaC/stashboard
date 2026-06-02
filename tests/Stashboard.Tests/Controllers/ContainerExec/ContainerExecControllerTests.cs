using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.ContainerExec;

/// <summary>
/// V5.7 — gating tests for <see cref="ContainerExecController"/>'s ticket
/// endpoint. The WebSocket upgrade itself needs a live socket (covered by the
/// shared session-pump tests), but the two-way authorization gate — global flag
/// + per-connection opt-in, owned connection — plus the "works for every host
/// type" rule and the container/command binding are all exercised here.
/// </summary>
public class ContainerExecControllerTests : IAsyncLifetime
{
    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly ContainerExecTicketService _ticketService;
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ContainerExecControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        _ticketService = new ContainerExecTicketService(
            Options.Create(new ContainerExecOptions()), TimeProvider.System);
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
    }

    public Task DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateTicket_Returns403_WhenGlobalFlagOff()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: false);

        var result = await ctrl.CreateTicket(conn.Id, "web-1", null, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId, DockerHostType.LocalSocket, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, "web-1", null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateTicket_Returns403_WhenPerConnectionOptInOff()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowExec: false);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, "web-1", null, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData(DockerHostType.LocalSocket)]
    [InlineData(DockerHostType.TcpTls)]
    [InlineData(DockerHostType.Ssh)]
    public async Task CreateTicket_ReturnsRedeemableTicket_ForEveryHostType(DockerHostType hostType)
    {
        // Container exec is *not* SSH-only — it works for any host type the
        // daemon can be reached on.
        var conn = await SeedConnectionAsync(_userId, hostType, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, "web-1", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ContainerExecTicketResponse>(ok.Value);
        Assert.False(string.IsNullOrEmpty(response.Ticket));
        Assert.Contains(conn.Id.ToString(), response.WebSocketPath);
        Assert.Contains("web-1", response.WebSocketPath);

        // The minted ticket is bound to (this user, connection, container) and
        // defaults the command to /bin/sh when none is supplied.
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(_userId, redeemed!.UserId);
        Assert.Equal(conn.Id, redeemed.ConnectionId);
        Assert.Equal("web-1", redeemed.ContainerName);
        Assert.Equal(new[] { "/bin/sh" }, redeemed.Command);
    }

    [Fact]
    public async Task CreateTicket_BindsSuppliedCommand()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, "db-1", new ContainerExecTicketRequest("/bin/bash"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ContainerExecTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(new[] { "/bin/bash" }, redeemed!.Command);
    }

    [Fact]
    public async Task CreateTicket_SplitsMultiTokenCommandIntoArgv()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, "web-1", new ContainerExecTicketRequest("/bin/sh -c env"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ContainerExecTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(new[] { "/bin/sh", "-c", "env" }, redeemed!.Command);
    }

    [Fact]
    public async Task CreateTicket_BlankCommand_FallsBackToDefault()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowExec: true);
        var ctrl = BuildController(allowExecGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, "web-1", new ContainerExecTicketRequest("   "), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ContainerExecTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(new[] { "/bin/sh" }, redeemed!.Command);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertStatus(ActionResult<ContainerExecTicketResponse> result, int expected)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expected, status.StatusCode);
    }

    private ContainerExecController BuildController(bool allowExecGlobal, Guid? userId = null)
    {
        var connectionMapper = new DockerConnectionMapper(_encryptionMock.Object);
        var settingsService = new ContainerExecSettingsService(
            _dbContext,
            Options.Create(new StashboardOptions { AllowContainerExec = allowExecGlobal }),
            TimeProvider.System);
        var controller = new ContainerExecController(
            _dbContext,
            connectionMapper,
            _ticketService,
            new ContainerExecSessionRegistry(Options.Create(new ContainerExecOptions())),
            Mock.Of<IContainerExecConnector>(),
            settingsService,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IHostApplicationLifetime>(),
            Options.Create(new ContainerExecOptions()),
            NullLogger<ContainerExecController>.Instance);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(
        Guid userId, DockerHostType hostType, bool allowExec)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = hostType,
            HostUrl = hostType == DockerHostType.TcpTls ? "tcp://10.0.0.5:2376" : null,
            SshHost = hostType == DockerHostType.Ssh ? "vps.example.com" : null,
            SshUsername = hostType == DockerHostType.Ssh ? "docker" : null,
            SshPrivateKeyEncrypted = hostType == DockerHostType.Ssh ? "enc:PEM" : null,
            SshRemoteSocketPath = hostType == DockerHostType.Ssh ? "/var/run/docker.sock" : null,
            AllowExec = allowExec,
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
        await _dbContext.ContainerExecSettings.ExecuteDeleteAsync();
        await _dbContext.DockerExecSessions.ExecuteDeleteAsync();
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

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests.db")};Pooling=False";
}
