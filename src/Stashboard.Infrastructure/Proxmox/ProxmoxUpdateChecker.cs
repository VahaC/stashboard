using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V6.0 — orchestrates a Proxmox update scan. Lists LXCs and reads the node's
/// own pending-update count over the REST API
/// (<see cref="IProxmoxApiClient"/>), then reads each running LXC's count over
/// SSH (<see cref="IProxmoxGuestInspector"/>). Pure orchestration: it owns no
/// transport, so it is unit-tested with both collaborators mocked, mirroring
/// <c>DockerUpdateChecker</c>.
/// </summary>
internal sealed class ProxmoxUpdateChecker(
    IProxmoxApiClient apiClient,
    IProxmoxGuestInspector guestInspector,
    ILogger<ProxmoxUpdateChecker> logger) : IProxmoxUpdateChecker
{
    public async Task<ProxmoxCheckResult> CheckAsync(
        ProxmoxConnectionProfile profile,
        IReadOnlySet<int>? disabledVmIds = null,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.UtcNow;

        // The LXC listing doubles as the API reachability/auth probe. If it
        // fails, the whole scan failed (nothing else would succeed either), so
        // report a connection-level error rather than a pile of guest errors.
        IReadOnlyList<ProxmoxLxcSummary> lxcs;
        try
        {
            lxcs = await apiClient.ListLxcAsync(profile, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox API unreachable for node {Node}", profile.NodeName);
            return new ProxmoxCheckResult([], checkedAt, $"Proxmox API unreachable: {ex.Message}");
        }

        // V6.14 — the QEMU VM listing rides on the same reachable host as the LXC
        // probe. Treat a failure here the same way (connection-level error, keep
        // the last-known cards) so a transient blip can't wipe the VM rows.
        IReadOnlyList<ProxmoxLxcSummary> vms;
        try
        {
            vms = await apiClient.ListQemuAsync(profile, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox QEMU listing failed for node {Node}", profile.NodeName);
            return new ProxmoxCheckResult([], checkedAt, $"Proxmox API unreachable: {ex.Message}");
        }

        var guests = new List<ProxmoxGuestResult>(lxcs.Count + vms.Count + 1)
        {
            await CheckNodeAsync(profile, cancellationToken),
        };

        foreach (var lxc in lxcs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // V6.7 — a disabled LXC is still listed (so its card and resource
            // details stay current) but its update count isn't computed: skip
            // the IP lookup + SSH `pct exec`, exactly as a disabled Docker watch
            // is left unchecked.
            if (disabledVmIds is not null && disabledVmIds.Contains(lxc.VmId))
            {
                guests.Add(Guest(lxc, lxc.IsRunning, pendingUpdates: null, error: null, ipAddress: null));
                continue;
            }

            guests.Add(await CheckLxcAsync(profile, lxc, cancellationToken));
        }

        // V6.14 — QEMU VMs are discovered with their resource detail (free from the
        // list call) but carry no pending-update count: a VM isn't necessarily
        // Debian and may have no SSH/guest-agent, so apt-style update monitoring is
        // LXC-only. No IP lookup either (a VM needs the guest agent for that).
        foreach (var vm in vms)
        {
            if (cancellationToken.IsCancellationRequested) break;
            guests.Add(Vm(vm));
        }

        return new ProxmoxCheckResult(guests, checkedAt, null);
    }

    private async Task<ProxmoxGuestResult> CheckNodeAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var count = await apiClient.GetNodePendingUpdatesAsync(profile, cancellationToken);
            return new ProxmoxGuestResult(0, profile.NodeName, ProxmoxGuestType.Node, true, count, null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox node apt/update failed for {Node}", profile.NodeName);
            return new ProxmoxGuestResult(0, profile.NodeName, ProxmoxGuestType.Node, true, null, ex.Message);
        }
    }

    private async Task<ProxmoxGuestResult> CheckLxcAsync(
        ProxmoxConnectionProfile profile, ProxmoxLxcSummary lxc, CancellationToken cancellationToken)
    {
        // Resource detail (uptime / cores / mem / disk / tags) is free from the
        // list call; the IP needs one extra best-effort call per running guest.
        if (!lxc.IsRunning)
            return Guest(lxc, false, null, null, ipAddress: null);

        var ip = await apiClient.GetLxcIpAddressAsync(profile, lxc.VmId, cancellationToken);

        if (profile.Ssh is null)
            return Guest(lxc, true, null,
                "SSH is not configured — per-container update counts require SSH access to the Proxmox host.",
                ip);

        var inspection = await guestInspector.CountPendingUpdatesAsync(profile, lxc.VmId, cancellationToken);
        return Guest(lxc, true, inspection.PendingUpdates, inspection.Error, ip);
    }

    private static ProxmoxGuestResult Guest(
        ProxmoxLxcSummary lxc, bool isRunning, int? pendingUpdates, string? error, string? ipAddress) =>
        new(
            lxc.VmId, lxc.Name, ProxmoxGuestType.Lxc, isRunning, pendingUpdates, error,
            IpAddress: ipAddress,
            UptimeSeconds: lxc.UptimeSeconds,
            CpuCores: lxc.CpuCores,
            MemoryBytes: lxc.MemoryBytes,
            DiskBytes: lxc.DiskBytes,
            Tags: lxc.Tags);

    /// <summary>V6.14 — a discovered QEMU VM as a guest row: resource detail from
    /// the list call, but never a pending-update count or IP (update monitoring
    /// and the SSH-backed IP lookup are LXC-only).</summary>
    private static ProxmoxGuestResult Vm(ProxmoxLxcSummary vm) =>
        new(
            vm.VmId, vm.Name, ProxmoxGuestType.Qemu, vm.IsRunning, PendingUpdates: null, Error: null,
            IpAddress: null,
            UptimeSeconds: vm.UptimeSeconds,
            CpuCores: vm.CpuCores,
            MemoryBytes: vm.MemoryBytes,
            DiskBytes: vm.DiskBytes,
            Tags: vm.Tags);

    public async Task<ProxmoxConnectionTestResult> TestConnectionAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var apiReachable = false;
        string? error = null;

        try
        {
            await apiClient.ListLxcAsync(profile, cancellationToken);
            apiReachable = true;
        }
        catch (Exception ex)
        {
            error = $"API: {ex.Message}";
        }

        bool? sshReachable = null;
        if (profile.Ssh is not null)
        {
            sshReachable = await guestInspector.TestConnectionAsync(profile, cancellationToken);
            if (sshReachable == false)
                error ??= "SSH connection failed — check host, username, and private key.";
        }

        return new ProxmoxConnectionTestResult(apiReachable, sshReachable, error);
    }
}
