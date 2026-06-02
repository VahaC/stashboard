using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.HostTerminal;

/// <summary>
/// V5.3 — gating tests for <see cref="HostTerminalController"/>'s ticket
/// endpoint. The WebSocket upgrade itself needs a live socket (covered by the
/// session pump tests + an integration harness), but the three-way authorization
/// gate — global flag, per-connection opt-in, owned SSH connection — is all
/// exercised here.
/// </summary>
public class HostTerminalControllerTests : IAsyncLifetime
{
    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly HostShellTicketService _ticketService;
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public HostTerminalControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        _ticketService = new HostShellTicketService(
            Options.Create(new HostShellOptions()), TimeProvider.System);
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
        var conn = await SeedConnectionAsync(_userId, DockerHostType.Ssh, allowHostShell: true);
        var ctrl = BuildController(allowHostShellGlobal: false);

        var result = await ctrl.CreateTicket(conn.Id, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_Returns404_WhenConnectionForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId, DockerHostType.Ssh, allowHostShell: true);
        var ctrl = BuildController(allowHostShellGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateTicket_Returns400_WhenConnectionNotSsh()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.LocalSocket, allowHostShell: false);
        var ctrl = BuildController(allowHostShellGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateTicket_Returns403_WhenPerConnectionOptInOff()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.Ssh, allowHostShell: false);
        var ctrl = BuildController(allowHostShellGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_ReturnsRedeemableTicket_WhenEligible()
    {
        var conn = await SeedConnectionAsync(_userId, DockerHostType.Ssh, allowHostShell: true);
        var ctrl = BuildController(allowHostShellGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<HostShellTicketResponse>(ok.Value);
        Assert.False(string.IsNullOrEmpty(response.Ticket));
        Assert.Contains(conn.Id.ToString(), response.WebSocketPath);

        // The minted ticket is bound to (this user, this connection).
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(_userId, redeemed!.UserId);
        Assert.Equal(conn.Id, redeemed.ConnectionId);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertStatus(ActionResult<HostShellTicketResponse> result, int expected)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expected, status.StatusCode);
    }

    private HostTerminalController BuildController(bool allowHostShellGlobal, Guid? userId = null)
    {
        var connectionMapper = new DockerConnectionMapper(_encryptionMock.Object);
        // Real settings service backed by the test DbContext, seeded from the
        // per-test global flag — its first read creates the singleton row.
        var settingsService = new HostShellSettingsService(
            _dbContext,
            Options.Create(new StashboardOptions { AllowHostShell = allowHostShellGlobal }),
            TimeProvider.System);
        var controller = new HostTerminalController(
            _dbContext,
            connectionMapper,
            _ticketService,
            new HostShellSessionRegistry(Options.Create(new HostShellOptions())),
            Mock.Of<IHostShellConnector>(),
            settingsService,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IHostApplicationLifetime>(),
            Options.Create(new HostShellOptions()),
            NullLogger<HostTerminalController>.Instance);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(
        Guid userId, DockerHostType hostType, bool allowHostShell)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = hostType,
            SshHost = hostType == DockerHostType.Ssh ? "vps.example.com" : null,
            SshUsername = hostType == DockerHostType.Ssh ? "docker" : null,
            SshPrivateKeyEncrypted = hostType == DockerHostType.Ssh ? "enc:PEM" : null,
            SshRemoteSocketPath = hostType == DockerHostType.Ssh ? "/var/run/docker.sock" : null,
            AllowHostShell = allowHostShell,
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
                // Recreate from the current model so a stale on-disk file from an
                // earlier build (without the V5.3 HostShellSessions table) is
                // rebuilt. Safe because the suite runs classes sequentially.
                await _dbContext.Database.EnsureDeletedAsync();
                await _dbContext.Database.EnsureCreatedAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
    }

    private async Task ClearAllDataAsync()
    {
        await _dbContext.HostShellSettings.ExecuteDeleteAsync();
        await _dbContext.HostShellSessions.ExecuteDeleteAsync();
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
