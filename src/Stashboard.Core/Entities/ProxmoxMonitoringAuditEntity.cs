using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.11 — audit row for a single LXC monitoring change: enable / disable
/// (per-guest or bulk), and snooze / unsnooze for a maintenance window. Written
/// once per affected guest at the moment of the change, so the operational
/// traceability already present for the console (<see cref="ProxmoxConsoleSessionEntity"/>)
/// and "Update now" (<see cref="ProxmoxUpdateSessionEntity"/>) actions extends to
/// monitoring state too — who changed what, on which guest, when, and the new
/// state. Surfaced read-only on the Settings → Audit page.
/// <para>
/// Like the other Proxmox audit rows the host / guest details are denormalised
/// onto the row (captured at write time) so the history survives a host rename or
/// delete; the connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxMonitoringAuditEntity : AuditableEntity
{
    /// <summary>User who made the change. Denormalised so the audit view never
    /// has to walk back through the connection (and so the row survives a
    /// connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>The Proxmox host the guest belongs to. Nullable + <c>SetNull</c>
    /// on delete so deleting a host keeps the historical rows around.</summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at change time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at change time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>VMID of the LXC whose monitoring changed.</summary>
    public int VmId { get; set; }

    /// <summary>Guest name captured at change time, for the audit view.</summary>
    [MaxLength(255)]
    public string? GuestName { get; set; }

    /// <summary>What changed (enable / disable / snooze / unsnooze).</summary>
    public ProxmoxMonitoringChangeType ChangeType { get; set; }

    /// <summary>The guest's monitoring-enabled flag <em>after</em> the change.</summary>
    public bool MonitoringEnabled { get; set; }

    /// <summary>For a <see cref="ProxmoxMonitoringChangeType.Snoozed"/> row, the
    /// UTC instant the snooze runs until; <c>null</c> for the other change
    /// types.</summary>
    public DateTime? SnoozedUntil { get; set; }

    /// <summary>Whether the change was part of a host-wide bulk action ("Enable
    /// all" / "Disable all"). Lets the audit view group / label bulk runs.</summary>
    public bool Bulk { get; set; }

    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}
