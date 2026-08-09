using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.Mqtt;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.1 — integration-style tests for <see cref="MqttEntityStateProvider"/>: the
/// derived-signal sources it builds from the DB + live Proxmox queries. Real SQLite,
/// mocked Proxmox API (backups) and Docker host client. Covers per-LXC / per-node
/// update counts, the node-alert problem sensor + category attributes, the newest
/// backup's ctime, and the estate roll-ups matching the underlying entities.
/// </summary>
public class MqttEntityStateProviderTests : IAsyncLifetime
{
    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private ApplicationDbContext _db = default!;
    private readonly Mock<IProxmoxApiClient> _apiMock = new();

    public async ValueTask InitializeAsync()
    {
        _db = CreateDbContext();
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task BuildsDerivedSignals_Counts_Alert_Backup_AndRollups()
    {
        var conn = await SeedConnectionAsync(reachable: true);

        // Node row (apt updates = 3) + two LXCs (one running w/ 2 updates, one stopped).
        AddGuest(conn.Id, vmId: 0, ProxmoxGuestType.Node, "pve", running: true, pending: 3);
        AddGuest(conn.Id, vmId: 101, ProxmoxGuestType.Lxc, "ct1", running: true, pending: 2);
        AddGuest(conn.Id, vmId: 102, ProxmoxGuestType.Lxc, "ct2", running: false, pending: null);

        // Node alert: CPU crit, memory warn, the rest clear.
        AddAlertState(conn.Id, ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 97, 90);
        AddAlertState(conn.Id, ProxmoxAlertCategory.Memory, HealthLevel.Warn, "RAM", 82, 80);

        // One health-enabled service that's up.
        _db.WebResources.Add(new WebResourceEntity
        {
            Id = Guid.NewGuid(), UserId = conn.UserId, Name = "Sonarr", MainUrl = "https://sonarr.lan",
            MainUrlHealthCheckEnabled = true, CurrentStatus = ServiceStatus.Up,
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Newest backup for ct1 (101) is the later of two archives.
        var older = DateTimeOffset.UtcNow.AddDays(-9).ToUnixTimeSeconds();
        var newer = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
        _apiMock.Setup(a => a.ListBackupsAsync(It.IsAny<ProxmoxConnectionProfile>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxBackup>
            {
                new("local:backup/vzdump-lxc-101-a.tar.zst", "local", 101, older, 1, "tar.zst", null),
                new("local:backup/vzdump-lxc-101-b.tar.zst", "local", 101, newer, 1, "tar.zst", null),
            });
        _apiMock.Setup(a => a.ListBackupsAsync(It.IsAny<ProxmoxConnectionProfile>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxBackup>());

        var entities = await BuildProvider().GetEntitiesAsync();

        // ── update counts ──
        Assert.Equal(3, NumberOf(entities, MqttEntityKind.UpdateCount, $"node:{conn.Id}"));
        Assert.Equal(2, NumberOf(entities, MqttEntityKind.UpdateCount, $"guest:{conn.Id}:101"));
        Assert.DoesNotContain(entities, e => e.Kind == MqttEntityKind.UpdateCount && e.DeviceKey == $"guest:{conn.Id}:102");

        // ── node-alert problem sensor + attributes ──
        var alert = entities.Single(e => e.Kind == MqttEntityKind.NodeAlert && e.DeviceKey == $"node:{conn.Id}");
        Assert.True(alert.IsOn);
        Assert.Equal("crit", alert.Attributes!["worst_severity"]);
        Assert.Equal("crit", alert.Attributes!["cpu"]);
        Assert.Equal("warn", alert.Attributes!["memory"]);
        Assert.Equal("ok", alert.Attributes!["storage"]);

        // ── newest backup ctime ──
        var backup = entities.Single(e => e.Kind == MqttEntityKind.BackupAge && e.DeviceKey == $"guest:{conn.Id}:101");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(newer), backup.Timestamp);

        // ── roll-ups match the underlying entities ──
        Assert.Equal(1, Rollup(entities, "guests_running"));   // ct1 running, ct2 stopped
        Assert.Equal(2, Rollup(entities, "guests_total"));
        Assert.Equal(1, Rollup(entities, "services_online"));
        Assert.Equal(1, Rollup(entities, "services_total"));
        Assert.Equal(5, Rollup(entities, "updates_pending"));  // 3 (node) + 2 (lxc)
        Assert.Equal(1, Rollup(entities, "hosts_reachable"));  // the one reachable PVE
    }

    [Fact]
    public async Task NodeAlert_ClearsWhenAllCategoriesResolve()
    {
        var conn = await SeedConnectionAsync(reachable: true);
        AddGuest(conn.Id, vmId: 0, ProxmoxGuestType.Node, "pve", running: true, pending: 0);
        AddAlertState(conn.Id, ProxmoxAlertCategory.Cpu, HealthLevel.Ok, "CPU", 12, 90);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var entities = await BuildProvider().GetEntitiesAsync();

        var alert = entities.Single(e => e.Kind == MqttEntityKind.NodeAlert && e.DeviceKey == $"node:{conn.Id}");
        Assert.False(alert.IsOn);
        Assert.Equal("ok", alert.Attributes!["worst_severity"]);
    }

    [Fact]
    public async Task BackupListing_IsCachedAcrossCycles()
    {
        var conn = await SeedConnectionAsync(reachable: true);
        AddGuest(conn.Id, vmId: 101, ProxmoxGuestType.Lxc, "ct1", running: true, pending: null);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _apiMock.Setup(a => a.ListBackupsAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxBackup>
            {
                new("local:backup/vzdump-lxc-101-a.tar.zst", "local", 101, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, "tar.zst", null),
            });

        // Same provider instance ⇒ shared cache; the second cycle must not re-list.
        var provider = BuildProvider();
        await provider.GetEntitiesAsync();
        await provider.GetEntitiesAsync();

        // lxc + qemu listed once each on the first cycle, nothing on the second.
        _apiMock.Verify(a => a.ListBackupsAsync(It.IsAny<ProxmoxConnectionProfile>(), false, It.IsAny<CancellationToken>()), Times.Once);
        _apiMock.Verify(a => a.ListBackupsAsync(It.IsAny<ProxmoxConnectionProfile>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnreachableHost_NotCountedInHostsReachableRollup()
    {
        await SeedConnectionAsync(reachable: false);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var entities = await BuildProvider().GetEntitiesAsync();

        Assert.Equal(0, Rollup(entities, "hosts_reachable"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static double NumberOf(IReadOnlyList<MqttSourceEntity> entities, MqttEntityKind kind, string deviceKey) =>
        entities.Single(e => e.Kind == kind && e.DeviceKey == deviceKey).Number!.Value;

    private static double Rollup(IReadOnlyList<MqttSourceEntity> entities, string metricKey) =>
        entities.Single(e => e.Kind == MqttEntityKind.Rollup && e.MetricKey == metricKey).Number!.Value;

    private MqttEntityStateProvider BuildProvider()
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");

        var dockerMapper = new Mock<IDockerConnectionMapper>();
        var dockerHost = new Mock<IDockerHostClient>();
        var proxmoxMapper = new ProxmoxConnectionMapper(encryption.Object);

        return new MqttEntityStateProvider(
            _db, dockerMapper.Object, dockerHost.Object, proxmoxMapper, _apiMock.Object,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<MqttEntityStateProvider>.Instance);
    }

    private async Task<ProxmoxConnectionEntity> SeedConnectionAsync(bool reachable)
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"owner-{Guid.NewGuid():N}@example.com",
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
            ServerType = ProxmoxServerType.Pve,
            Enabled = true,
            LastCheckedUtc = reachable ? DateTime.UtcNow : null,
            LastError = reachable ? null : "timed out",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxConnections.Add(conn);
        await _db.SaveChangesAsync();
        return conn;
    }

    private void AddGuest(Guid connId, int vmId, ProxmoxGuestType type, string name, bool running, int? pending)
    {
        _db.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            Id = Guid.NewGuid(),
            ProxmoxConnectionId = connId,
            VmId = vmId,
            GuestType = type,
            Name = name,
            IsRunning = running,
            MonitoringEnabled = true,
            PendingUpdates = pending,
            UpdatedUtc = DateTime.UtcNow,
        });
    }

    private void AddAlertState(Guid connId, ProxmoxAlertCategory category, HealthLevel level, string metric, double value, double threshold)
    {
        _db.ProxmoxNodeAlertStates.Add(new ProxmoxNodeAlertStateEntity
        {
            Id = Guid.NewGuid(),
            ProxmoxConnectionId = connId,
            Category = category,
            ActiveLevel = level,
            Metric = metric,
            Value = value,
            Threshold = threshold,
            UpdatedUtc = DateTime.UtcNow,
        });
    }

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
        await _db.ProxmoxGuests.ExecuteDeleteAsync();
        await _db.ProxmoxConnections.ExecuteDeleteAsync();
        await _db.WebResources.ExecuteDeleteAsync();
        await _db.DockerWatches.ExecuteDeleteAsync();
        await _db.DockerConnections.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-mqtt-provider-tests.db")};Pooling=False";
}

