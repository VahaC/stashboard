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
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.ProxmoxConsole;

/// <summary>
/// V8.6 — gating tests for <see cref="ProxmoxVncConsoleController"/>'s ticket
/// endpoint. The WebSocket upgrade + RFB relay need a live socket (the byte pump is
/// covered by <see cref="Services.ProxmoxConsole.ProxmoxVncSessionTests"/>), but the
/// two-way authorization gate — global flag + per-host opt-in, owned host — is
/// exercised here, plus the key VM-specific behaviours: gate failures short-circuit
/// <em>before</em> any host call, a successful gate mints a VNC session and returns a
/// redeemable ticket carrying the RFB password, and a host that refuses the VNC relay
/// surfaces as a clean 409. Mirrors the V6.6
/// <see cref="ProxmoxConsoleControllerTests"/>. Note the seeded host has <em>no</em>
/// SSH — the VM console must work without it.
/// </summary>
public class ProxmoxVncConsoleControllerTests : IAsyncLifetime
{
    private Guid _userId;
    private Guid _otherUserId;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly ProxmoxVncTicketService _ticketService;
    private IDataFactory _dataFactory = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ProxmoxVncConsoleControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        _ticketService = new ProxmoxVncTicketService(
            Options.Create(new ProxmoxConsoleOptions()), TimeProvider.System);
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
    }

    public ValueTask DisposeAsync() => _dbContext.DisposeAsync();

    [Fact]
    public async Task CreateTicket_Returns403_WhenGlobalFlagOff_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var relay = new Mock<IProxmoxVncRelay>();
        var ctrl = BuildController(allowConsoleGlobal: false, relay);

        var result = await ctrl.CreateTicket(conn.Id, 110, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
        relay.Verify(r => r.OpenVncProxyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTicket_Returns404_WhenHostForeign_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_otherUserId, allowConsole: true);
        var relay = new Mock<IProxmoxVncRelay>();
        var ctrl = BuildController(allowConsoleGlobal: true, relay);

        var result = await ctrl.CreateTicket(conn.Id, 110, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        relay.Verify(r => r.OpenVncProxyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTicket_Returns403_WhenPerHostOptInOff_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: false);
        var relay = new Mock<IProxmoxVncRelay>();
        var ctrl = BuildController(allowConsoleGlobal: true, relay);

        var result = await ctrl.CreateTicket(conn.Id, 110, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
        relay.Verify(r => r.OpenVncProxyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTicket_BothGatesOn_MintsVncSession_AndReturnsRedeemableTicketWithPassword()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var relay = new Mock<IProxmoxVncRelay>();
        relay.Setup(r => r.OpenVncProxyAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 110, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxVncProxyTicket("PVEVNC:deadbeef", 5901));
        var ctrl = BuildController(allowConsoleGlobal: true, relay);

        var result = await ctrl.CreateTicket(conn.Id, 110, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxVncConsoleTicketResponse>(ok.Value);
        Assert.False(string.IsNullOrEmpty(response.Ticket));
        // The RFB password handed to noVNC is the ephemeral vncproxy ticket — never
        // the API token.
        Assert.Equal("PVEVNC:deadbeef", response.Password);
        Assert.Contains(conn.Id.ToString(), response.WebSocketPath);
        Assert.Contains("/qemu/110/", response.WebSocketPath);

        // The minted ticket is bound to (this user, connection, vmid, vncProxy).
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(_userId, redeemed!.UserId);
        Assert.Equal(conn.Id, redeemed.ConnectionId);
        Assert.Equal(110, redeemed.VmId);
        Assert.Equal(5901, redeemed.VncProxy.Port);
        Assert.Equal("PVEVNC:deadbeef", redeemed.VncProxy.Ticket);
    }

    [Fact]
    public async Task CreateTicket_Returns409_WhenHostRefusesVncProxy()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var relay = new Mock<IProxmoxVncRelay>();
        relay.Setup(r => r.OpenVncProxyAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Proxmox returned 500: VM 110 not running"));
        var ctrl = BuildController(allowConsoleGlobal: true, relay);

        var result = await ctrl.CreateTicket(conn.Id, 110, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status409Conflict);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertStatus(ActionResult<ProxmoxVncConsoleTicketResponse> result, int expected)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expected, status.StatusCode);
    }

    private ProxmoxVncConsoleController BuildController(
        bool allowConsoleGlobal, Mock<IProxmoxVncRelay> relay, Guid? userId = null)
    {
        var connectionMapper = new ProxmoxConnectionMapper(_encryptionMock.Object);
        var settingsService = new ProxmoxConsoleSettingsService(
            _dbContext,
            Options.Create(new StashboardOptions { AllowProxmoxConsole = allowConsoleGlobal }),
            TimeProvider.System);
        var controller = new ProxmoxVncConsoleController(
            _dbContext,
            connectionMapper,
            _ticketService,
            new ProxmoxConsoleSessionRegistry(Options.Create(new ProxmoxConsoleOptions())),
            relay.Object,
            settingsService,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IHostApplicationLifetime>(),
            Options.Create(new ProxmoxConsoleOptions()),
            NullLogger<ProxmoxVncConsoleController>.Instance);

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
        // Deliberately no SSH material — the VM console uses the API token, not SSH.
        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:secret",
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
        await _dbContext.ProxmoxConsoleSessions.ExecuteDeleteAsync();
        await _dbContext.ProxmoxGuests.ExecuteDeleteAsync();
        await _dbContext.ProxmoxConnections.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests-proxmox-vnc-console.db")};Pooling=False";
}



