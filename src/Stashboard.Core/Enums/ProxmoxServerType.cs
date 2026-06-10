namespace Stashboard.Core.Enums;

/// <summary>
/// V6.8.3 — which Proxmox product a connection points at. The two share the same
/// `/api2/json` REST surface and node-level endpoints (status, rrddata, disks,
/// SMART, network, apt) but differ in the API-token <em>auth header</em>
/// (<c>PVEAPIToken</c> uses <c>=</c> between id and secret, <c>PBSAPIToken</c>
/// uses <c>:</c>) and in a few resource endpoints (PVE has LXC guests +
/// <c>/storage</c>; PBS has no guests and exposes <c>datastores</c> instead).
/// The connection carries this so the API client can branch on it.
/// </summary>
public enum ProxmoxServerType
{
    /// <summary>Proxmox VE (virtual environment) — hosts LXC/VMs. Default.</summary>
    Pve = 0,

    /// <summary>Proxmox Backup Server — no guests; monitored as a node + its
    /// datastores.</summary>
    Pbs = 1,
}
