using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Tests.Services;

/// <summary>
/// Integration-style tests for <see cref="ProxmoxUpdateBackgroundService"/>.
/// Real SQLite test DB, mocked checker (so we control the discovered guests
/// without a live Proxmox host) and mocked email/Telegram senders. Mirrors
/// <see cref="DockerUpdateBackgroundServiceTests"/>.
/// </summary>
public class ProxmoxUpdateBackgroundServiceTests : IAsyncLifetime
{
    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private ApplicationDbContext _db = default!;
    private readonly Mock<IProxmoxUpdateChecker> _checkerMock = new();
    private readonly Mock<IEmailSender> _emailMock = new();
    private readonly Mock<ITelegramSender> _telegramMock = new();
    // V6.11 — the real scan queue, shared between the service under test and the
    // webhook-drain tests so an enqueue is visible to the drain.
    private readonly Stashboard.Infrastructure.Proxmox.ProxmoxScanQueue _scanQueue = new();

    public async Task InitializeAsync()
    {
        _db = CreateDbContext();
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── DoD: a due connection's guests are discovered + stamped ───────────────

    [Fact]
    public async Task ScanOnce_DueConnection_UpsertsGuestsAndStampsTimestamp()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        var checkedAt = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        SetupCheck(new ProxmoxCheckResult(
        [
            new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 4, null),
            new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, 7, null),
        ], checkedAt, null));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        var row = await ReloadConnectionAsync(conn.Id);
        Assert.Equal(checkedAt, row!.LastCheckedUtc);
        Assert.Null(row.LastError);

        var guests = await _db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == conn.Id).ToListAsync();
        Assert.Equal(2, guests.Count);
        Assert.Equal(4, guests.Single(g => g.VmId == 0).PendingUpdates);
        Assert.Equal(7, guests.Single(g => g.VmId == 105).PendingUpdates);
    }

    [Fact]
    public async Task ScanOnce_NotDue_IsSkipped()
    {
        var user = await SeedUserAsync();
        await SeedConnectionAsync(user.Id, lastCheckedUtc: DateTime.UtcNow.AddMinutes(-10), checkEveryHours: 6);

        await BuildService().ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScanOnce_DisabledConnection_IsSkipped()
    {
        var user = await SeedUserAsync();
        await SeedConnectionAsync(user.Id, lastCheckedUtc: null, enabled: false);

        await BuildService().ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_PendingUpdates_SendsEmailAndStampsSignature()
    {
        var user = await SeedUserAsync("alice@example.com");
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        SetupCheck(Result(node: 2, pihole: 5));

        EmailMessage? captured = null;
        _emailMock.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await BuildService().ScanOnceAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("alice@example.com", captured!.To);
        var row = await ReloadConnectionAsync(conn.Id);
        Assert.False(string.IsNullOrEmpty(row!.LastNotifiedSignature));
        Assert.NotNull(row.LastNotificationSentUtc);
    }

    [Fact]
    public async Task ScanOnce_SameSignatureAcrossTwoScans_OnlyEmailsOnce()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null, checkEveryHours: 1);

        SetupCheck(Result(node: 2, pihole: 5));
        _emailMock.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildService();
        await sut.ScanOnceAsync(CancellationToken.None);
        await MakeDueAgainAsync(conn.Id);
        await sut.ScanOnceAsync(CancellationToken.None);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanOnce_NoPendingUpdates_NoEmail()
    {
        var user = await SeedUserAsync();
        await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        SetupCheck(Result(node: 0, pihole: 0));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V6.7 per-guest monitoring toggle ──────────────────────────────────────

    [Fact]
    public async Task ScanOnce_DisabledGuest_IsPassedToCheckerAndToggleSurvives()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);
        // A previously-discovered LXC the user turned monitoring off for.
        await SeedGuestAsync(conn.Id, vmId: 105, name: "pihole", monitoringEnabled: false);

        IReadOnlySet<int>? capturedDisabled = null;
        _checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<ProxmoxConnectionProfile, IReadOnlySet<int>?, CancellationToken>((_, d, _) => capturedDisabled = d)
            .ReturnsAsync(new ProxmoxCheckResult(
            [
                new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 1, null),
                // Mirrors the real checker: a disabled guest comes back with a
                // null count (it was skipped).
                new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, null, null),
            ], DateTime.UtcNow, null));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        // The scan handed the disabled vmid to the checker so it can skip it.
        Assert.NotNull(capturedDisabled);
        Assert.Contains(105, capturedDisabled!);

        // Re-discovery preserves the user's toggle (matched on connection+vmid).
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.False(guest.MonitoringEnabled);
    }

    [Fact]
    public async Task ScanOnce_NewlyDiscoveredGuest_DefaultsToMonitoringEnabled()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        SetupCheck(Result(node: 0, pihole: 0));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.True(guest.MonitoringEnabled);
    }

    [Fact]
    public async Task ScanOnce_DisabledGuestWithPending_IsExcludedFromNotification()
    {
        var user = await SeedUserAsync("alice@example.com");
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);
        await SeedGuestAsync(conn.Id, vmId: 105, name: "pihole", monitoringEnabled: false);

        // Even if a count leaks through for the disabled guest, the notifier must
        // exclude it — and with only the node at 0 there's nothing to email.
        SetupCheck(new ProxmoxCheckResult(
        [
            new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 0, null),
            new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, 5, null),
        ], DateTime.UtcNow, null));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V6.11 snooze (excluded until expiry, then auto-re-included) ────────────

    [Fact]
    public async Task ScanOnce_SnoozedGuest_IsExcludedFromChecks()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);
        // Monitoring on, but snoozed for a maintenance window in the future.
        await SeedGuestAsync(conn.Id, vmId: 105, name: "pihole", monitoringEnabled: true,
            snoozedUntil: DateTime.UtcNow.AddHours(2));

        IReadOnlySet<int>? capturedSkip = null;
        _checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<ProxmoxConnectionProfile, IReadOnlySet<int>?, CancellationToken>((_, d, _) => capturedSkip = d)
            .ReturnsAsync(new ProxmoxCheckResult(
            [
                new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 1, null),
                new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, null, null),
            ], DateTime.UtcNow, null));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        Assert.NotNull(capturedSkip);
        Assert.Contains(105, capturedSkip!);

        // The future snooze survives the scan.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.NotNull(guest.MonitoringSnoozedUntil);
    }

    [Fact]
    public async Task ScanOnce_ExpiredSnooze_IsClearedAndReIncluded()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);
        // Snooze window already elapsed — the scan should clear it and check again.
        await SeedGuestAsync(conn.Id, vmId: 105, name: "pihole", monitoringEnabled: true,
            snoozedUntil: DateTime.UtcNow.AddHours(-1));

        IReadOnlySet<int>? capturedSkip = null;
        _checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<ProxmoxConnectionProfile, IReadOnlySet<int>?, CancellationToken>((_, d, _) => capturedSkip = d)
            .ReturnsAsync(new ProxmoxCheckResult(
            [
                new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 1, null),
                new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, 4, null),
            ], DateTime.UtcNow, null));

        await BuildService().ScanOnceAsync(CancellationToken.None);

        // Not skipped — the elapsed snooze auto-re-included the guest.
        Assert.NotNull(capturedSkip);
        Assert.DoesNotContain(105, capturedSkip!);

        // And the field was cleared.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.Null(guest.MonitoringSnoozedUntil);
        Assert.Equal(4, guest.PendingUpdates);
    }

    // ── V6.11 webhook-triggered scan drain ─────────────────────────────────────

    [Fact]
    public async Task DrainScanQueueOnce_EnqueuedConnection_IsScanned()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: DateTime.UtcNow, checkEveryHours: 24);
        // Not due on the schedule — only the webhook drain should scan it.
        SetupCheck(Result(node: 1, pihole: 2));
        _scanQueue.TryEnqueue(conn.Id);

        await BuildService().DrainScanQueueOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var guests = await _db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == conn.Id).ToListAsync();
        Assert.Equal(2, guests.Count);
    }

    [Fact]
    public async Task DrainScanQueueOnce_DisabledConnection_IsNotScanned()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null, enabled: false);
        _scanQueue.TryEnqueue(conn.Id);

        await BuildService().DrainScanQueueOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Resilience ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_ConnectionLevelError_StampsErrorKeepsExistingGuestsNoEmail()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        // First scan: discover a guest.
        SetupCheck(Result(node: 1, pihole: 3));
        var sut = BuildService();
        await sut.ScanOnceAsync(CancellationToken.None);
        await MakeDueAgainAsync(conn.Id);

        // Second scan: API unreachable — guests must survive, error recorded.
        SetupCheck(new ProxmoxCheckResult([], DateTime.UtcNow, "Proxmox API unreachable: boom"));
        await sut.ScanOnceAsync(CancellationToken.None);

        var row = await ReloadConnectionAsync(conn.Id);
        Assert.Contains("boom", row!.LastError);
        var guests = await _db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == conn.Id).ToListAsync();
        Assert.Equal(2, guests.Count); // node + pihole preserved
    }

    [Fact]
    public async Task ScanOnce_GuestDisappears_RowIsRemoved()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, lastCheckedUtc: null);

        SetupCheck(Result(node: 0, pihole: 0));
        var sut = BuildService();
        await sut.ScanOnceAsync(CancellationToken.None);
        await MakeDueAgainAsync(conn.Id);

        // pihole (105) gone — only the node row remains.
        SetupCheck(new ProxmoxCheckResult(
            [new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, 0, null)],
            DateTime.UtcNow, null));
        await sut.ScanOnceAsync(CancellationToken.None);

        var guests = await _db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == conn.Id).ToListAsync();
        Assert.Single(guests);
        Assert.Equal(0, guests[0].VmId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ProxmoxCheckResult Result(int node, int pihole) =>
        new(
        [
            new ProxmoxGuestResult(0, "pve", ProxmoxGuestType.Node, true, node, null),
            new ProxmoxGuestResult(105, "pihole", ProxmoxGuestType.Lxc, true, pihole, null),
        ], DateTime.UtcNow, null);

    private void SetupCheck(ProxmoxCheckResult result) =>
        _checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<IReadOnlySet<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private ProxmoxUpdateBackgroundService BuildService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(BuildTestConnectionString()));
        services.AddSingleton(_checkerMock.Object);
        services.AddSingleton(_emailMock.Object);
        services.AddSingleton(_telegramMock.Object);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        services.AddSingleton(encryption.Object);

        services.AddScoped<IProxmoxConnectionMapper, ProxmoxConnectionMapper>();
        services.AddScoped<IProxmoxUpdateNotificationService, ProxmoxUpdateNotificationService>();
        services.AddScoped<IProxmoxScanService, ProxmoxScanService>();

        services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.Configure<JwtOptions>(o =>
        {
            o.Secret = "test-secret-test-secret-test-secret-test-secret";
            o.EmailConfirmationTokenLifetimeHours = 24;
            o.PasswordResetTokenLifetimeHours = 2;
        });
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<IUserService, UserService>();
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var monitor = new TestOptionsMonitor(new ProxmoxUpdateOptions { TickIntervalSeconds = 300 });
        return new ProxmoxUpdateBackgroundService(scopeFactory, _scanQueue, monitor,
            NullLogger<ProxmoxUpdateBackgroundService>.Instance);
    }

    private async Task MakeDueAgainAsync(Guid connectionId)
    {
        var conn = await _db.ProxmoxConnections.AsTracking().SingleAsync(c => c.Id == connectionId);
        conn.LastCheckedUtc = DateTime.UtcNow.AddHours(-48);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<ProxmoxConnectionEntity> SeedConnectionAsync(
        Guid userId, DateTime? lastCheckedUtc, int checkEveryHours = 24, bool enabled = true)
    {
        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:secret",
            Enabled = enabled,
            ScheduleType = CheckScheduleType.Hourly,
            CheckEveryHours = checkEveryHours,
            LastCheckedUtc = lastCheckedUtc,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxConnections.Add(conn);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return conn;
    }

    private async Task SeedGuestAsync(
        Guid connectionId, int vmId, string name, bool monitoringEnabled, DateTime? snoozedUntil = null)
    {
        _db.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            Id = Guid.NewGuid(),
            ProxmoxConnectionId = connectionId,
            VmId = vmId,
            Name = name,
            GuestType = ProxmoxGuestType.Lxc,
            IsRunning = true,
            MonitoringEnabled = monitoringEnabled,
            MonitoringSnoozedUntil = snoozedUntil,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<UserEntity> SeedUserAsync(string email = "owner@example.com")
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = $"{email.ToUpperInvariant()}-{Guid.NewGuid():N}",
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return user;
    }

    private Task<ProxmoxConnectionEntity?> ReloadConnectionAsync(Guid id) =>
        _db.ProxmoxConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);

    // ── DB plumbing (mirrors DockerUpdateBackgroundServiceTests) ──────────────

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
        await _db.ProxmoxGuests.ExecuteDeleteAsync();
        await _db.ProxmoxConnections.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests.db")};Pooling=False";

    private sealed class TestOptionsMonitor(ProxmoxUpdateOptions value) : IOptionsMonitor<ProxmoxUpdateOptions>
    {
        public ProxmoxUpdateOptions CurrentValue { get; } = value;
        public ProxmoxUpdateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ProxmoxUpdateOptions, string?> listener) => null;
    }
}
