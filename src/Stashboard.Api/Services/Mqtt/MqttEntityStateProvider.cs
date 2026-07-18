using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — the live implementation of <see cref="IMqttEntityStateProvider"/>.
/// Reads service health + Proxmox guest state straight from the DB (both are
/// persisted by their background loops), and queries each Docker daemon for the
/// container running-state already shown on the Docker page (best-effort: a host
/// that's unreachable on a tick is skipped, not failed). Docker image-update
/// signals come from the persisted <see cref="DockerWatchEntity.UpdateStatus"/>.
/// </summary>
public sealed class MqttEntityStateProvider(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IDockerHostClient hostClient,
    ILogger<MqttEntityStateProvider> logger) : IMqttEntityStateProvider
{
    public async Task<IReadOnlyList<MqttSourceEntity>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var entities = new List<MqttSourceEntity>();

        await AddServiceHealthAsync(entities, cancellationToken);
        await AddProxmoxGuestsAsync(entities, cancellationToken);
        await AddDockerAsync(entities, cancellationToken);

        return entities;
    }

    private async Task AddServiceHealthAsync(List<MqttSourceEntity> entities, CancellationToken cancellationToken)
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

            entities.Add(new MqttSourceEntity(
                MqttEntityKind.Health,
                DeviceKey: deviceKey,
                DeviceName: deviceName,
                DeviceModel: deviceModel,
                IsOn: s.CurrentStatus switch
                {
                    ServiceStatus.Up or ServiceStatus.NeedsAttention => true,
                    ServiceStatus.Down => false,
                    _ => null,
                }));
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

    private async Task AddProxmoxGuestsAsync(List<MqttSourceEntity> entities, CancellationToken cancellationToken)
    {
        var guests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.GuestType != ProxmoxGuestType.Node)
            .Select(g => new { g.ProxmoxConnectionId, g.VmId, g.Name, g.GuestType, g.IsRunning })
            .ToListAsync(cancellationToken);

        foreach (var g in guests)
        {
            entities.Add(new MqttSourceEntity(
                MqttEntityKind.Running,
                DeviceKey: $"guest:{g.ProxmoxConnectionId}:{g.VmId}",
                DeviceName: g.Name,
                DeviceModel: g.GuestType == ProxmoxGuestType.Qemu ? "Proxmox VM" : "Proxmox LXC",
                IsOn: g.IsRunning));
        }
    }

    private async Task AddDockerAsync(List<MqttSourceEntity> entities, CancellationToken cancellationToken)
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
                foreach (var c in details)
                {
                    entities.Add(new MqttSourceEntity(
                        MqttEntityKind.Running,
                        DeviceKey: $"docker:{connection.Id}:{c.Name}",
                        DeviceName: c.Name,
                        DeviceModel: "Docker container",
                        IsOn: string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)));
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
    }
}
