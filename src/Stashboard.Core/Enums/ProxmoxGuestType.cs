namespace Stashboard.Core.Enums;

/// <summary>
/// V6.0 — what a <c>ProxmoxGuestEntity</c> row represents. The Proxmox node
/// itself is tracked as a guest row (<see cref="Node"/>) alongside the LXC
/// containers (<see cref="Lxc"/>) and QEMU VMs (<see cref="Qemu"/>) it hosts, so
/// the dashboard can render one card per object — host + guests — from a single
/// collection. Proxmox vmids are unique cluster-wide across LXC and QEMU, so
/// (connection, vmid) stays a clean natural key across all three types.
/// </summary>
public enum ProxmoxGuestType
{
    /// <summary>The Proxmox VE node (hypervisor host). Update count comes from
    /// the purpose-built <c>GET /nodes/{node}/apt/update</c> API endpoint.</summary>
    Node = 0,

    /// <summary>An LXC container on the node. Update count comes from
    /// <c>pct exec &lt;vmid&gt; -- apt list --upgradable</c> over SSH (the
    /// Proxmox API exposes no per-LXC package list).</summary>
    Lxc = 1,

    /// <summary>V6.14 — a QEMU/KVM virtual machine on the node, listed via
    /// <c>GET /nodes/{node}/qemu</c>. Lifecycle + live/historical stats + tasks
    /// reuse the LXC surface, but a VM carries no pending-update count: it isn't
    /// necessarily Debian and may have no SSH/guest-agent, so apt-style update
    /// monitoring (and the SSH-backed console) is LXC-only for now.</summary>
    Qemu = 2,
}
