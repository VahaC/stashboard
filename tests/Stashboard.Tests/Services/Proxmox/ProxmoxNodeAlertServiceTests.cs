using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Tests.Services.Proxmox;

/// <summary>
/// V6.8.1 — integration-style tests for <see cref="ProxmoxNodeAlertService"/>.
/// Real SQLite DB + real notifier (mocked senders), mocked Proxmox API/SSH.
/// Covers the end-to-end wiring: debounce (fires only after N breaches),
/// per-node override resolution, throttle (no duplicate notification), recovery,
/// and "an unavailable source never alerts".
/// </summary>
public class ProxmoxNodeAlertServiceTests : IAsyncLifetime
{
    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private ApplicationDbContext _db = default!;
    private readonly Mock<IProxmoxApiClient> _apiMock = new();
    private readonly Mock<IProxmoxGuestInspector> _inspectorMock = new();
    private readonly Mock<IEmailSender> _emailMock = new();
    private readonly Mock<ITelegramSender> _telegramMock = new();
    private readonly Mock<IUserService> _userMock = new();

    public async ValueTask InitializeAsync()
    {
        _db = CreateDbContext();
        await EnsureSchemaAsync();
        await ClearAllDataAsync();

        // Sensible "nothing else breaching / nothing available" defaults so each
        // test only has to set the metric it cares about.
        _apiMock.Setup(a => a.GetNodeStoragesAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _apiMock.Setup(a => a.GetNodeDisksAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _inspectorMock.Setup(i => i.ReadNodeSensorsAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxNodeSensors(false, "n/a", []));
        _inspectorMock.Setup(i => i.ReadNodeNetworkErrorsAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxNodeNetworkErrors(false, "n/a", 0));
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    // ── debounce ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FiresOnlyAfterNConsecutiveBreaches()
    {
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        SetupCpu(0.97);   // 97% → crit
        var sut = BuildService(breaches: 2);

        await sut.EvaluateConnectionAsync(conn);
        VerifyNoEmail();
        Assert.Null(await ActiveCpuAsync(conn.Id));   // pending, not yet active

        await sut.EvaluateConnectionAsync(conn);
        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        var active = await ActiveCpuAsync(conn.Id);
        Assert.NotNull(active);
        Assert.Equal(HealthLevel.Crit, active!.ActiveLevel);
        Assert.NotNull(active.FirstSeenUtc);
    }

    // ── unavailable source never alerts ───────────────────────────────────────

    [Fact]
    public async Task UnavailableSource_NeverAlerts()
    {
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        _apiMock.Setup(a => a.GetNodeStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("node unreachable"));
        var sut = BuildService(breaches: 1);

        for (var i = 0; i < 3; i++) await sut.EvaluateConnectionAsync(conn);

        VerifyNoEmail();
        Assert.Null(await ActiveCpuAsync(conn.Id));
    }

    // ── per-node override resolution ──────────────────────────────────────────

    [Fact]
    public async Task PerNodeOverride_SuppressesAlert_AHotNodeCanBeTuned()
    {
        // 90% CPU would warn under the global default (80), but this node is
        // deliberately hot: override warn→92, crit→98, so 90% is Ok.
        var (conn, _) = await SeedAsync(
            categoryMask: ProxmoxAlertCategory.Cpu, cpuWarn: 92, cpuCrit: 98);
        SetupCpu(0.90);
        var sut = BuildService(breaches: 1);

        await sut.EvaluateConnectionAsync(conn);

        VerifyNoEmail();
        Assert.Null(await ActiveCpuAsync(conn.Id));
    }

    [Fact]
    public async Task DefaultThresholds_AlertAt90Percent()
    {
        // Same 90% but no override — the global default warn (80) fires.
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        SetupCpu(0.90);
        var sut = BuildService(breaches: 1);

        await sut.EvaluateConnectionAsync(conn);

        var active = await ActiveCpuAsync(conn.Id);
        Assert.NotNull(active);
        Assert.Equal(HealthLevel.Warn, active!.ActiveLevel);
    }

    // ── throttle ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnchangedAlert_NotifiesOnlyOnce()
    {
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        SetupCpu(0.97);
        var sut = BuildService(breaches: 1);

        await sut.EvaluateConnectionAsync(conn);
        await sut.EvaluateConnectionAsync(conn);
        await sut.EvaluateConnectionAsync(conn);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SteadySeverity_WithFluctuatingValue_NotifiesOnce()
    {
        // The throttle keys on category+severity, not the value — a deviation
        // that stays Warn while its value wiggles (85→88→90 %) must not re-ping.
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        var sut = BuildService(breaches: 1);

        SetupCpu(0.85); await sut.EvaluateConnectionAsync(conn);
        SetupCpu(0.88); await sut.EvaluateConnectionAsync(conn);
        SetupCpu(0.90); await sut.EvaluateConnectionAsync(conn);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── recovery ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recovers_WhenMetricReturnsToOk()
    {
        var (conn, _) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu);
        var sut = BuildService(breaches: 1);

        SetupCpu(0.97);
        await sut.EvaluateConnectionAsync(conn);
        Assert.NotNull(await ActiveCpuAsync(conn.Id));

        SetupCpu(0.10);   // back to normal
        await sut.EvaluateConnectionAsync(conn);

        Assert.Null(await ActiveCpuAsync(conn.Id));
        // Two emails total: the alert and the all-clear.
        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DisabledSettings_AreSkipped_NoProxmoxCalls()
    {
        var (conn, settings) = await SeedAsync(categoryMask: ProxmoxAlertCategory.Cpu, enabled: false);
        SetupCpu(0.97);
        var sut = BuildService(breaches: 1);

        await sut.EvaluateConnectionAsync(conn);

        _apiMock.Verify(a => a.GetNodeStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoEmail();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetupCpu(double fraction) =>
        _apiMock.Setup(a => a.GetNodeStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NodeStatus(fraction));

    private static ProxmoxNodeStatus NodeStatus(double cpuFraction) => new(
        CpuModel: "test", Sockets: 1, Cpus: 4, Cores: 4, CpuMhz: 2400, Hvm: true,
        CpuFraction: cpuFraction, IoWaitFraction: 0, Load1: 1, Load5: 1, Load15: 1,
        MemTotal: 8_000_000_000, MemUsed: 1_000_000_000, MemFree: 7_000_000_000, SwapTotal: 0, SwapUsed: 0,
        RootTotal: 100_000_000_000, RootUsed: 10_000_000_000,
        UptimeSeconds: 1000, KernelVersion: "x", PveVersion: "8", SubscriptionStatus: null, SubscriptionLevel: null);

    private void VerifyNoEmail() =>
        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);

    private Task<ProxmoxNodeAlertStateEntity?> ActiveCpuAsync(Guid connId) =>
        _db.ProxmoxNodeAlertStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProxmoxConnectionId == connId
                && s.Category == ProxmoxAlertCategory.Cpu && s.ActiveLevel != HealthLevel.Ok);

    private ProxmoxNodeAlertService BuildService(int breaches)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");

        var mapper = new ProxmoxConnectionMapper(encryption.Object);
        var appriseSender = new Mock<IAppriseSender>();
        var appriseSettings = new Mock<IAppriseSettingsService>();
        appriseSettings.Setup(s => s.GetResolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAppriseSettings(false, "", []));
        var notifier = new ProxmoxNodeAlertNotificationService(
            _emailMock.Object, _telegramMock.Object, appriseSender.Object, appriseSettings.Object,
            encryption.Object, NullLogger<ProxmoxNodeAlertNotificationService>.Instance);
        var options = new TestOptionsMonitor(new ProxmoxUpdateOptions { AlertConsecutiveBreaches = breaches });

        return new ProxmoxNodeAlertService(
            _db, mapper, _apiMock.Object, _inspectorMock.Object, notifier, _userMock.Object, options,
            NullLogger<ProxmoxNodeAlertService>.Instance);
    }

    private async Task<(ProxmoxConnectionEntity, ProxmoxNodeAlertSettingsEntity)> SeedAsync(
        ProxmoxAlertCategory categoryMask, bool enabled = true,
        int? cpuWarn = null, int? cpuCrit = null)
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "owner@example.com",
            NormalizedEmail = $"OWNER-{Guid.NewGuid():N}",
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(user);

        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:secret",
            UpdateNotificationsEnabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxConnections.Add(conn);

        var settings = new ProxmoxNodeAlertSettingsEntity
        {
            Id = Guid.NewGuid(),
            ProxmoxConnectionId = conn.Id,
            Enabled = enabled,
            CategoryMask = categoryMask,
            CpuWarn = cpuWarn,
            CpuCrit = cpuCrit,
            CreatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxNodeAlertSettings.Add(settings);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _userMock.Setup(u => u.FindByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        return (conn, settings);
    }

    // ── DB plumbing (mirrors ProxmoxUpdateBackgroundServiceTests) ──────────────

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await _db.Database.EnsureDeletedAsync();
                await _db.Database.EnsureCreatedAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
    }

    private async Task ClearAllDataAsync()
    {
        await _db.ProxmoxNodeAlertStates.ExecuteDeleteAsync();
        await _db.ProxmoxNodeAlertSettings.ExecuteDeleteAsync();
        await _db.ProxmoxConnections.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-alert-tests.db")};Pooling=False";

    private sealed class TestOptionsMonitor(ProxmoxUpdateOptions value) : IOptionsMonitor<ProxmoxUpdateOptions>
    {
        public ProxmoxUpdateOptions CurrentValue { get; } = value;
        public ProxmoxUpdateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ProxmoxUpdateOptions, string?> listener) => null;
    }
}
