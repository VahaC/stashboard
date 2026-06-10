using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.0 — a single object discovered on a <see cref="ProxmoxConnectionEntity"/>
/// during a scan: either the node itself (<see cref="ProxmoxGuestType.Node"/>)
/// or one of its LXC containers (<see cref="ProxmoxGuestType.Lxc"/>). Rows are
/// upserted each scan keyed by (connection, vmid) — discovery is automatic, the
/// user never adds guests by hand. One card per row on the dashboard.
/// </summary>
public class ProxmoxGuestEntity : BaseEntity
{
    /// <summary>Owning connection. Deleting the connection cascades its guest
    /// rows away — they have no meaning without the host.</summary>
    public Guid ProxmoxConnectionId { get; set; }
    public ProxmoxConnectionEntity ProxmoxConnection { get; set; } = default!;

    /// <summary>Proxmox numeric id. <c>0</c> is reserved for the
    /// <see cref="ProxmoxGuestType.Node"/> row (real LXC vmids start at 100),
    /// so (connection, vmid) is a clean natural key across both types.</summary>
    public int VmId { get; set; }

    public ProxmoxGuestType GuestType { get; set; }

    /// <summary>Display name — the LXC hostname, or the node name for the node
    /// row.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = default!;

    /// <summary>
    /// V6.7 — per-LXC update-monitoring toggle, the Proxmox analogue of
    /// <c>DockerWatchEntity.Enabled</c>. When <c>false</c> the guest is skipped
    /// by scheduled and manual update-count checks and excluded from
    /// notifications, exactly like a disabled Docker watch. Defaults to
    /// <c>true</c> for newly discovered guests (and is backfilled to <c>true</c>
    /// for rows that predate this column), and is preserved across re-discovery
    /// keyed on (connection, vmid). The node row (<c>VmId == 0</c>) stays
    /// host-controlled and is never toggled here.
    /// </summary>
    public bool MonitoringEnabled { get; set; } = true;

    /// <summary>
    /// V6.11 — maintenance-window snooze: while set to a future UTC instant the
    /// guest is treated as if monitoring were off (skipped by scheduled and
    /// manual update checks and excluded from notifications), then automatically
    /// re-included once the instant passes — the scan service clears the field on
    /// the first scan at/after it. Independent of <see cref="MonitoringEnabled"/>:
    /// a guest can be monitored normally yet snoozed for a window, and a disabled
    /// guest stays disabled regardless of any snooze. <c>null</c> means "not
    /// snoozed". Never set for the node row (<c>VmId == 0</c>).
    /// </summary>
    public DateTime? MonitoringSnoozedUntil { get; set; }

    /// <summary>Whether the guest was running at scan time. A stopped LXC can't
    /// be exec'd, so <see cref="PendingUpdates"/> is left <c>null</c> for it.
    /// Always <c>true</c> for the node row.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Pending package update count from the last successful check, or
    /// <c>null</c> when it couldn't be determined (guest stopped, not Debian,
    /// SSH/exec failed).</summary>
    public int? PendingUpdates { get; set; }

    /// <summary>Per-guest error from the most recent check (e.g. the LXC isn't
    /// Debian-based, or <c>pct exec</c> failed). <c>null</c> on success.</summary>
    public string? LastError { get; set; }

    /// <summary>Primary IPv4 of a running LXC (from the Proxmox interfaces API),
    /// or <c>null</c>. Not set for the node row.</summary>
    [MaxLength(64)]
    public string? IpAddress { get; set; }

    /// <summary>Seconds since the guest started (running guests), else <c>null</c>.</summary>
    public long? UptimeSeconds { get; set; }

    /// <summary>Configured CPU core count.</summary>
    public int? CpuCores { get; set; }

    /// <summary>Memory limit in bytes (<c>maxmem</c>).</summary>
    public long? MemoryBytes { get; set; }

    /// <summary>Root disk size in bytes (<c>maxdisk</c>).</summary>
    public long? DiskBytes { get; set; }

    /// <summary>Raw semicolon-separated Proxmox tags, or <c>null</c>.</summary>
    [MaxLength(500)]
    public string? Tags { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
