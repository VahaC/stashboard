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
/// V6.6 — gating tests for <see cref="ProxmoxConsoleController"/>'s ticket
/// endpoint. The WebSocket upgrade itself needs a live socket (covered by the
/// shared session-pump tests), but the two-way authorization gate — global flag
/// + per-host opt-in, owned host — plus the LXC/command binding are exercised
/// here. Mirrors the V5.7 container-exec controller tests.
/// </summary>
public class ProxmoxConsoleControllerTests : IAsyncLifetime
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

    public ProxmoxConsoleControllerTests()
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
    public async Task CreateTicket_Returns403_WhenGlobalFlagOff()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: false);

        var result = await ctrl.CreateTicket(conn.Id, 105, null, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_Returns404_WhenHostForeign()
    {
        var conn = await SeedConnectionAsync(_otherUserId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateTicket_Returns403_WhenPerHostOptInOff()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: false);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, null, CancellationToken.None);

        AssertStatus(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CreateTicket_BothGatesOn_ReturnsRedeemableTicket_DefaultsToBash()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(conn.Id, 105, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConsoleTicketResponse>(ok.Value);
        Assert.False(string.IsNullOrEmpty(response.Ticket));
        Assert.Contains(conn.Id.ToString(), response.WebSocketPath);
        Assert.Contains("/lxc/105/", response.WebSocketPath);

        // The minted ticket is bound to (this user, connection, vmid) and
        // defaults the command to /bin/bash when none is supplied.
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(_userId, redeemed!.UserId);
        Assert.Equal(conn.Id, redeemed.ConnectionId);
        Assert.Equal(105, redeemed.VmId);
        Assert.Equal(new[] { "/bin/bash" }, redeemed.Command);
    }

    [Fact]
    public async Task CreateTicket_BindsSuppliedCommand()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, 110, new ProxmoxConsoleTicketRequest("/bin/sh"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConsoleTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(110, redeemed!.VmId);
        Assert.Equal(new[] { "/bin/sh" }, redeemed.Command);
    }

    [Fact]
    public async Task CreateTicket_SplitsMultiTokenCommandIntoArgv()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, 105, new ProxmoxConsoleTicketRequest("/bin/sh -c env"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConsoleTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(new[] { "/bin/sh", "-c", "env" }, redeemed!.Command);
    }

    [Fact]
    public async Task CreateTicket_BlankCommand_FallsBackToDefault()
    {
        var conn = await SeedConnectionAsync(_userId, allowConsole: true);
        var ctrl = BuildController(allowConsoleGlobal: true);

        var result = await ctrl.CreateTicket(
            conn.Id, 105, new ProxmoxConsoleTicketRequest("   "), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConsoleTicketResponse>(ok.Value);
        var redeemed = _ticketService.Redeem(response.Ticket);
        Assert.NotNull(redeemed);
        Assert.Equal(new[] { "/bin/bash" }, redeemed!.Command);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertStatus(ActionResult<ProxmoxConsoleTicketResponse> result, int expected)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expected, status.StatusCode);
    }

    private ProxmoxConsoleController BuildController(bool allowConsoleGlobal, Guid? userId = null)
    {
        var connectionMapper = new ProxmoxConnectionMapper(_encryptionMock.Object);
        var settingsService = new ProxmoxConsoleSettingsService(
            _dbContext,
            Options.Create(new StashboardOptions { AllowProxmoxConsole = allowConsoleGlobal }),
            TimeProvider.System);
        var controller = new ProxmoxConsoleController(
            _dbContext,
            connectionMapper,
            _ticketService,
            new ProxmoxConsoleSessionRegistry(Options.Create(new ProxmoxConsoleOptions())),
            Mock.Of<IHostShellConnector>(),
            settingsService,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IHostApplicationLifetime>(),
            Options.Create(new ProxmoxConsoleOptions()),
            NullLogger<ProxmoxConsoleController>.Instance);

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
        await _dbContext.ProxmoxConsoleSessions.ExecuteDeleteAsync();
        await _dbContext.ProxmoxGuests.ExecuteDeleteAsync();
        await _dbContext.ProxmoxConnections.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests-proxmox-console.db")};Pooling=False";
}



