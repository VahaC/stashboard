using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.ProxmoxLogs;

/// <summary>
/// V6.12 — gating tests for <see cref="ProxmoxLogsController"/>'s ticket endpoint.
/// The live tail rides the V6.6 console transport, so the gate is identical:
/// global flag + per-host opt-in + owned host. The WebSocket upgrade itself needs
/// a live socket (covered by the shared session-pump tests). Mirrors
/// <see cref="Stashboard.Tests.Controllers.ProxmoxConsole.ProxmoxConsoleControllerTests"/>.
/// </summary>
public class ProxmoxLogsControllerTests : IAsyncLifetime
{
    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly ProxmoxConsoleTicketService _ticketService;
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ProxmoxLogsControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        _ticketService = new ProxmoxConsoleTicketService(
            Options.Create(new ProxmoxConsoleOptions()), TimeProvider.System);
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
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: false);

        var result = await ctrl.CreateTicket(conn.Id, 105, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_Returns404_WhenHostForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateTicket_Returns403_WhenPerHostOptInOff()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: false);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_BothGatesOn_ReturnsRedeemableTicket_BoundToTarget()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxLogsTicketResponse>(ok.Value);
        Assert.False(string.IsNullOrEmpty(response.Ticket));
        Assert.Contains(conn.Id.ToString(), response.WebSocketPath);
        Assert.Contains("/lxc/105/logs/ws", response.WebSocketPath);

        // The minted ticket is bound to (this user, connection, vmid). It carries
        // no command — the read-only journal tail is built server-side — so an
        // empty argv also means it can't be cross-redeemed for a shell.
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(_userId, redeemed!.UserId);
        Assert.Equal(conn.Id, redeemed.ConnectionId);
        Assert.Equal(105, redeemed.VmId);
        Assert.Empty(redeemed.Command);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertStatus(ActionResult<ProxmoxLogsTicketResponse> result, int expected)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expected, status.StatusCode);
    }

    private ProxmoxLogsController BuildController(bool allowConsoleGlobal, Guid? userId = null)
    {
        var connectionMapper = new ProxmoxConnectionMapper(_encryptionMock.Object);
        var settingsService = new ProxmoxConsoleSettingsService(
            _dbContext,
            Options.Create(new StashboardOptions { AllowProxmoxConsole = allowConsoleGlobal }),
            TimeProvider.System);
        var controller = new ProxmoxLogsController(
            _dbContext,
            connectionMapper,
            _ticketService,
            new ProxmoxConsoleSessionRegistry(Options.Create(new ProxmoxConsoleOptions())),
            Mock.Of<IHostShellConnector>(),
            settingsService,
            Mock.Of<IHostApplicationLifetime>(),
            Options.Create(new ProxmoxConsoleOptions()),
            NullLogger<ProxmoxLogsController>.Instance);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private async Task<ProxmoxConnectionEntity> SeedConnectionAsync(Guid userId, bool allowConsole)
    {
        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            SshHost = "pve.lan",
            SshUsername = "root",
            SshPrivateKeyEncrypted = "enc:PEM",
            AllowConsole = allowConsole,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.ProxmoxConnections.Add(conn);
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
        await _dbContext.ProxmoxConsoleSettings.ExecuteDeleteAsync();
        await _dbContext.ProxmoxGuests.ExecuteDeleteAsync();
        await _dbContext.ProxmoxConnections.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests-proxmox-logs.db")};Pooling=False";
}
