using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 / V9.1 — the live implementation of <see cref="IMqttEntityStateProvider"/>.
/// Reads service health + Proxmox guest state straight from the DB (both are
/// persisted by their background loops), and queries each Docker daemon for the
/// container running-state already shown on the Docker page (best-effort: a host
/// that's unreachable on a tick is skipped, not failed).
/// <para>V9.1 additionally surfaces the <em>derived</em> signals Stashboard already
/// computes: pending-update counts (per Docker host / Proxmox node / LXC), the
/// V6.8.1 node-alert verdicts (per PVE/PBS node, device_class <c>problem</c>), the age
/// of each guest's newest vzdump backup, and a Stashboard hub of estate roll-ups.
/// Pure publish — no new collection.</para>
/// </summary>
public sealed class MqttEntityStateProvider(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IDockerHostClient hostClient,
    IProxmoxConnectionMapper proxmoxMapper,
    IProxmoxApiClient proxmoxApi,
    IMemoryCache cache,
    ILogger<MqttEntityStateProvider> logger) : IMqttEntityStateProvider
{
    // Backups complete roughly daily and listing storage content is the heaviest read
    // here, so cache the newest-ctime-per-guest map per host rather than re-listing every
    // ~30s publish cycle. Still well within a "check cycle" for a freshness signal.
    private static readonly TimeSpan BackupCacheTtl = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<MqttSourceEntity>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var entities = new List<MqttSourceEntity>();
        var rollup = new RollupCounters();

        await AddServiceHealthAsync(entities, rollup, cancellationToken);
        await AddProxmoxGuestsAsync(entities, rollup, cancellationToken);
        await AddProxmoxNodesAsync(entities, rollup, cancellationToken);
        await AddDockerAsync(entities, rollup, cancellationToken);
        await AddBackupAgesAsync(entities, cancellationToken);
        AddRollups(entities, rollup);

        return entities;
    }

    private async Task AddServiceHealthAsync(List<MqttSourceEntity> entities, RollupCounters rollup, CancellationToken cancellationToken)
    {
        var services = await db.WebResources.AsNoTracking()
            .Where(s => s.MainUrlHealthCheckEnabled
                || (s.AdditionalUrl != null && s.AdditionalUrlHealthCheckEnabled))
            .Select(s => new { s.Id, s.Name, s.CurrentStatus })
            .ToListAsync(cancellationToken);

        // A service's health sensor groups onto the SAME HA device as the container /
        // guest the user linked it to — so "jellyfin" shows health next to running +
        // update — but only when the service maps to exactly ONE object. A container and
        // an LXC that merely share a name are never merged: they have distinct DeviceKeys
        // and a service linked to neither (or to several) keeps its own device.
        var containerLinks = (await db.DockerWatches.AsNoTracking()
            .Where(w => w.WebResourceId != null)
            .Select(w => new { WebResourceId = w.WebResourceId!.Value, w.DockerConnectionId, w.ContainerName })
            .ToListAsync(cancellationToken))
            .Select(w => (w.WebResourceId, w.DockerConnectionId, w.ContainerName))
            .ToList();
        var guestLinks = (await db.WebResourceProxmoxGuestLinks.AsNoTracking()
            .Select(l => new { l.WebResourceId, l.ProxmoxConnectionId, l.VmId })
            .ToListAsync(cancellationToken))
            .Select(l => (l.WebResourceId, l.ProxmoxConnectionId, l.VmId))
            .ToList();
        var guestNames = (await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.GuestType != ProxmoxGuestType.Node)
            .Select(g => new { g.ProxmoxConnectionId, g.VmId, g.Name, g.GuestType })
            .ToListAsync(cancellationToken))
            .ToDictionary(g => (g.ProxmoxConnectionId, g.VmId), g => (g.Name, g.GuestType));

        foreach (var s in services)
        {
            var (deviceKey, deviceName, deviceModel) = ResolveHealthDevice(
                s.Id, s.Name, containerLinks, guestLinks, guestNames);

            var online = s.CurrentStatus switch
            {
                ServiceStatus.Up or ServiceStatus.NeedsAttention => (bool?)true,
                ServiceStatus.Down => false,
                _ => null,
            };

            rollup.ServicesTotal++;
            if (online == true) rollup.ServicesOnline++;

            entities.Add(new MqttSourceEntity(
                MqttEntityKind.Health,
                DeviceKey: deviceKey,
                DeviceName: deviceName,
                DeviceModel: deviceModel,
                IsOn: online));
        }
    }

    /// <summary>
    /// Decides which device a service's health sensor belongs to. When the service is
    /// linked to exactly one Docker container (via a watch) or one Proxmox guest, the
    /// health rides on that object's device (matching the container/guest DeviceKey +
    /// name so they merge). Otherwise the service is its own device.
    /// </summary>
    private static (string DeviceKey, string DeviceName, string DeviceModel) ResolveHealthDevice(
        Guid serviceId, string serviceName,
        List<(Guid WebResourceId, Guid ConnId, string ContainerName)> containerLinks,
        List<(Guid WebResourceId, Guid ConnId, int VmId)> guestLinks,
        IReadOnlyDictionary<(Guid, int), (string Name, ProxmoxGuestType Type)> guestNames)
    {
        var containers = containerLinks.Where(c => c.WebResourceId == serviceId).ToList();
        var guests = guestLinks.Where(g => g.WebResourceId == serviceId).ToList();

        if (containers.Count + guests.Count == 1)
        {
            if (containers.Count == 1)
            {
                var c = containers[0];
                return ($"docker:{c.ConnId}:{c.ContainerName}", c.ContainerName, "Docker container");
            }

            var g = guests[0];
            var key = $"guest:{g.ConnId}:{g.VmId}";
            if (guestNames.TryGetValue((g.ConnId, g.VmId), out var guest))
                return (key, guest.Name, guest.Type == ProxmoxGuestType.Qemu ? "Proxmox VM" : "Proxmox LXC");
            // Linked guest not (yet) discovered — fall back to a guest-keyed device.
            return (key, serviceName, "Proxmox guest");
        }

        // Unlinked or ambiguous (multiple targets) → the service is its own device.
        return ($"service:{serviceId}", serviceName, "Service");
    }

    private async Task AddProxmoxGuestsAsync(List<MqttSourceEntity> entities, RollupCounters rollup, CancellationToken cancellationToken)
    {
        var guests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.GuestType != ProxmoxGuestType.Node)
            .Select(g => new { g.ProxmoxConnectionId, g.VmId, g.Name, g.GuestType, g.IsRunning, g.MonitoringEnabled, g.PendingUpdates })
            .ToListAsync(cancellationToken);

        foreach (var g in guests)
        {
            var deviceKey = $"guest:{g.ProxmoxConnectionId}:{g.VmId}";
            var deviceModel = g.GuestType == ProxmoxGuestType.Qemu ? "Proxmox VM" : "Proxmox LXC";

            rollup.GuestsTotal++;
            if (g.IsRunning) rollup.GuestsRunning++;

            entities.Add(new MqttSourceEntity(
                MqttEntityKind.Running,
                DeviceKey: deviceKey,
                DeviceName: g.Name,
                DeviceModel: deviceModel,
                IsOn: g.IsRunning));

            // V9.1 — pending-update count for a monitored LXC (the number behind the
            // V9.0 boolean). VMs/stopped/non-Debian guests have a null count and get
            // no count sensor.
            if (g.MonitoringEnabled && g.PendingUpdates is { } pending)
            {
                rollup.TotalUpdates += pending;
                entities.Add(new MqttSourceEntity(
                    MqttEntityKind.UpdateCount,
                    DeviceKey: deviceKey,
                    DeviceName: g.Name,
                    DeviceModel: deviceModel,
                    Number: pending));
            }
        }
    }

    /// <summary>
    /// V9.1 — per PVE/PBS node: the V6.8.1 node-alert verdict (a <c>problem</c>
    /// binary_sensor with the per-category breakdown + worst severity as attributes)
    /// and the node's apt update count. Both ride one <c>node:{connId}</c> device.
    /// Also tallies host reachability for the roll-up.
    /// </summary>
    private async Task AddProxmoxNodesAsync(List<MqttSourceEntity> entities, RollupCounters rollup, CancellationToken cancellationToken)
    {
        var connections = await db.ProxmoxConnections.AsNoTracking()
            .Where(c => c.Enabled)
            .Select(c => new { c.Id, c.Name, c.NodeName, c.ServerType, c.LastError, c.LastCheckedUtc })
            .ToListAsync(cancellationToken);

        // Node rows (VmId 0) carry the node name + node apt update count.
        var nodeRows = (await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.GuestType == ProxmoxGuestType.Node)
            .Select(g => new { g.ProxmoxConnectionId, g.Name, g.PendingUpdates })
            .ToListAsync(cancellationToken))
            .ToDictionary(g => g.ProxmoxConnectionId, g => (g.Name, g.PendingUpdates));

        var alertStates = (await db.ProxmoxNodeAlertStates.AsNoTracking()
            .Select(s => new { s.ProxmoxConnectionId, s.Category, s.ActiveLevel, s.Metric, s.Value, s.Threshold })
            .ToListAsync(cancellationToken))
            .GroupBy(s => s.ProxmoxConnectionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => new AlertCategoryState(s.Category, s.ActiveLevel, s.Metric, s.Value, s.Threshold)).ToList());

        foreach (var c in connections)
        {
            // A node is "reachable" when its last scheduled scan/eval succeeded.
            if (c.LastCheckedUtc is not null && string.IsNullOrEmpty(c.LastError))
                rollup.HostsReachable++;

            var deviceKey = $"node:{c.Id}";
            var deviceModel = c.ServerType == ProxmoxServerType.Pbs ? "Proxmox Backup Server" : "Proxmox node";
            var deviceName = nodeRows.TryGetValue(c.Id, out var node) && !string.IsNullOrWhiteSpace(node.Name)
                ? node.Name
                : c.NodeName;

            // Node apt update count.
            if (nodeRows.TryGetValue(c.Id, out var n) && n.PendingUpdates is { } nodePending)
            {
                rollup.TotalUpdates += nodePending;
                entities.Add(new MqttSourceEntity(
                    MqttEntityKind.UpdateCount,
                    DeviceKey: deviceKey,
                    DeviceName: deviceName,
                    DeviceModel: deviceModel,
                    Number: nodePending));
            }

            // Node-alert verdict — only when alerting has been evaluated for this host.
            if (alertStates.TryGetValue(c.Id, out var states))
            {
                var worst = HealthLevel.Ok;
                foreach (var s in states)
                    if (s.ActiveLevel > worst) worst = s.ActiveLevel;

                var attributes = BuildAlertAttributes(states, worst);
                entities.Add(new MqttSourceEntity(
                    MqttEntityKind.NodeAlert,
                    DeviceKey: deviceKey,
                    DeviceName: deviceName,
                    DeviceModel: deviceModel,
                    IsOn: worst >= HealthLevel.Warn,
                    Attributes: attributes));
            }
        }
    }

    /// <summary>One category's persisted alert state, projected for attribute building.</summary>
    private sealed record AlertCategoryState(
        ProxmoxAlertCategory Category, HealthLevel ActiveLevel, string? Metric, double? Value, double? Threshold);

    /// <summary>Builds the node-alert sensor's JSON attributes: a level per category
    /// (CPU / memory / storage / thermal / SMART / network), the worst active severity,
    /// and a short detail for the worst breaching metric.</summary>
    private static Dictionary<string, object?> BuildAlertAttributes(
        IReadOnlyList<AlertCategoryState> states, HealthLevel worst)
    {
        var byCategory = states.ToDictionary(s => s.Category);

        string Sev(ProxmoxAlertCategory cat) =>
            (byCategory.TryGetValue(cat, out var v) ? v.ActiveLevel : HealthLevel.Ok).ToString().ToLowerInvariant();

        var attributes = new Dictionary<string, object?>
        {
            ["worst_severity"] = worst.ToString().ToLowerInvariant(),
            ["cpu"] = Sev(ProxmoxAlertCategory.Cpu),
            ["memory"] = Sev(ProxmoxAlertCategory.Memory),
            ["storage"] = Sev(ProxmoxAlertCategory.Storage),
            ["thermal"] = Sev(ProxmoxAlertCategory.Thermal),
            ["smart"] = Sev(ProxmoxAlertCategory.Smart),
            ["network"] = Sev(ProxmoxAlertCategory.Network),
        };

        // Surface the worst active category's metric detail (e.g. "RAM 92 / 90").
        var worstActive = states
            .Where(s => s.ActiveLevel >= HealthLevel.Warn)
            .OrderByDescending(s => s.ActiveLevel)
            .ThenBy(s => s.Category)
            .FirstOrDefault();
        if (worstActive?.Metric is { } metric)
            attributes["detail"] = worstActive.Value is { } val && worstActive.Threshold is { } thr
                ? string.Format(CultureInfo.InvariantCulture, "{0} {1:0.#} / {2:0.#}", metric, val, thr)
                : metric;

        return attributes;
    }

    private async Task AddDockerAsync(List<MqttSourceEntity> entities, RollupCounters rollup, CancellationToken cancellationToken)
    {
        var connections = await db.DockerConnections.AsNoTracking().ToListAsync(cancellationToken);
        var watches = await db.DockerWatches.AsNoTracking()
            .Select(w => new { w.DockerConnectionId, w.ContainerName, w.UpdateStatus })
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            // Container running-state — live from the daemon, exactly as the Docker
            // instances page reads it. Best-effort: a host that's unreachable this
            // tick contributes no running sensors (its retained state is left as-is).
            try
            {
                var transport = connectionMapper.BuildTransport(connection);
                var details = await hostClient.ListContainerDetailsAsync(transport, cancellationToken);
                rollup.HostsReachable++;
                foreach (var c in details)
                {
                    var running = string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase);
                    rollup.ContainersTotal++;
                    if (running) rollup.ContainersRunning++;

                    entities.Add(new MqttSourceEntity(
                        MqttEntityKind.Running,
                        DeviceKey: $"docker:{connection.Id}:{c.Name}",
                        DeviceName: c.Name,
                        DeviceModel: "Docker container",
                        IsOn: running));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "MQTT: Docker host {Connection} unreachable; skipping its container sensors", connection.Name);
            }
        }

        // Image-update available — from the persisted watch status, grouped onto the
        // same device as the container's running sensor (shared DeviceKey).
        foreach (var w in watches)
        {
            entities.Add(new MqttSourceEntity(
                MqttEntityKind.Update,
                DeviceKey: $"docker:{w.DockerConnectionId}:{w.ContainerName}",
                DeviceName: w.ContainerName,
                DeviceModel: "Docker container",
                IsOn: w.UpdateStatus switch
                {
                    DockerUpdateStatus.UpdateAvailable => true,
                    DockerUpdateStatus.UpToDate => false,
                    _ => null,
                }));
        }

        // V9.1 — pending-update count per Docker host (the number behind the per-container
        // boolean), on its own `dockerhost:{connId}` device.
        foreach (var byHost in watches.GroupBy(w => w.DockerConnectionId))
        {
            var connection = connections.FirstOrDefault(c => c.Id == byHost.Key);
            if (connection is null) continue;

            var pending = byHost.Count(w => w.UpdateStatus == DockerUpdateStatus.UpdateAvailable);
            rollup.TotalUpdates += pending;
            entities.Add(new MqttSourceEntity(
                MqttEntityKind.UpdateCount,
                DeviceKey: $"dockerhost:{connection.Id}",
                DeviceName: connection.Name,
                DeviceModel: "Docker host",
                Number: pending));
        }
    }

    /// <summary>
    /// V9.1 — per-guest backup freshness: the creation time of the most recent vzdump
    /// archive across the node's backup storages, published as a <c>timestamp</c> sensor
    /// so HA renders "x days ago" and can alert on a stale backup. Best-effort and live
    /// (like the Docker query): a host that can't be reached this tick contributes no
    /// backup sensors. Attaches to the existing guest device.
    /// </summary>
    private async Task AddBackupAgesAsync(List<MqttSourceEntity> entities, CancellationToken cancellationToken)
    {
        var connections = await db.ProxmoxConnections.AsNoTracking()
            .Where(c => c.Enabled && c.ServerType == ProxmoxServerType.Pve)
            .ToListAsync(cancellationToken);

        // Only emit for guests that exist as devices, matching V9.0's name + model so the
        // sensor lands on the right device.
        var guests = (await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.GuestType != ProxmoxGuestType.Node)
            .Select(g => new { g.ProxmoxConnectionId, g.VmId, g.Name, g.GuestType })
            .ToListAsync(cancellationToken))
            .ToDictionary(g => (g.ProxmoxConnectionId, g.VmId), g => (g.Name, g.GuestType));

        foreach (var connection in connections)
        {
            try
            {
                var newest = await GetNewestBackupCtimesAsync(connection, cancellationToken);
                foreach (var (vmId, ctime) in newest)
                {
                    if (!guests.TryGetValue((connection.Id, vmId), out var guest)) continue;
                    entities.Add(new MqttSourceEntity(
                        MqttEntityKind.BackupAge,
                        DeviceKey: $"guest:{connection.Id}:{vmId}",
                        DeviceName: guest.Name,
                        DeviceModel: guest.GuestType == ProxmoxGuestType.Qemu ? "Proxmox VM" : "Proxmox LXC",
                        Timestamp: DateTimeOffset.FromUnixTimeSeconds(ctime)));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "MQTT: Proxmox host {Connection} unreachable; skipping its backup-age sensors", connection.Name);
            }
        }
    }

    /// <summary>Newest vzdump ctime per guest for a host, cached for
    /// <see cref="BackupCacheTtl"/> so the heavy storage-content listing isn't repeated
    /// every publish cycle.</summary>
    private async Task<IReadOnlyDictionary<int, long>> GetNewestBackupCtimesAsync(
        ProxmoxConnectionEntity connection, CancellationToken cancellationToken)
    {
        var key = $"mqtt:backup-ctimes:{connection.Id}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<int, long>? cached) && cached is not null)
            return cached;

        var profile = proxmoxMapper.BuildProfile(connection);
        var newest = new Dictionary<int, long>();
        foreach (var qemu in new[] { false, true })
        {
            var backups = await proxmoxApi.ListBackupsAsync(profile, qemu, cancellationToken);
            foreach (var b in backups)
            {
                if (b.VmId is not { } vmId || b.CTime is not { } ctime) continue;
                if (!newest.TryGetValue(vmId, out var existing) || ctime > existing)
                    newest[vmId] = ctime;
            }
        }

        cache.Set(key, (IReadOnlyDictionary<int, long>)newest, BackupCacheTtl);
        return newest;
    }

    /// <summary>V9.1 — the single Stashboard hub device's estate roll-up sensors: the
    /// one-glance + single-trigger surface over everything above.</summary>
    private static void AddRollups(List<MqttSourceEntity> entities, RollupCounters r)
    {
        void Add(string metricKey, double value) => entities.Add(new MqttSourceEntity(
            MqttEntityKind.Rollup, DeviceKey: "hub", DeviceName: "Stashboard", DeviceModel: "Integration",
            Number: value, MetricKey: metricKey));

        Add("containers_running", r.ContainersRunning);
        Add("containers_total", r.ContainersTotal);
        Add("guests_running", r.GuestsRunning);
        Add("guests_total", r.GuestsTotal);
        Add("services_online", r.ServicesOnline);
        Add("services_total", r.ServicesTotal);
        Add("hosts_reachable", r.HostsReachable);
        Add("updates_pending", r.TotalUpdates);
    }

    private sealed class RollupCounters
    {
        public int ContainersRunning;
        public int ContainersTotal;
        public int GuestsRunning;
        public int GuestsTotal;
        public int ServicesOnline;
        public int ServicesTotal;
        public int HostsReachable;
        public int TotalUpdates;
    }
}
