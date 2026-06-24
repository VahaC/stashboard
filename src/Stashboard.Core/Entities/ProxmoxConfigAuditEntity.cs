using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V8.5 — audit row for a single guest <strong>config edit</strong>: an LXC config
/// write (V6.5 / V6.9, retrofitted here) or a VM config write / disk grow / disk move
/// (V8.5). One row is written for every attempt that reaches the Proxmox API — whether
/// it succeeds or the host rejects it — so the day-to-day "tweak a guest" loop is fully
/// traceable (who, when, which host / node / vmid / kind, what changed, and the
/// result). Mirrors <see cref="ProxmoxCloneAuditEntity"/>: a single instantaneous
/// action, so it records a <see cref="CreatedAtUtc"/> instant plus a success flag
/// rather than start/end timestamps.
/// <para>
/// Like the other Proxmox audit rows the host details are denormalised onto the row
/// (captured at write time) so the history survives a host rename or delete; the
/// connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxConfigAuditEntity : AuditableEntity
{
    /// <summary>User who triggered the change.</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>The Proxmox host the guest belongs to. Nullable + <c>SetNull</c>
    /// on delete so deleting a host keeps the historical rows around.</summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at action time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at action time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>The guest whose config was edited.</summary>
    public int VmId { get; set; }

    /// <summary>Guest kind — <c>lxc</c> or <c>qemu</c> — so the one audit trail
    /// covers both container and VM config edits.</summary>
    [MaxLength(16)]
    public string GuestKind { get; set; } = "lxc";

    /// <summary>Which write this row records — <c>config</c> (a PUT …/config),
    /// <c>resize</c> (grow a disk), or <c>move-disk</c>.</summary>
    [MaxLength(32)]
    public string Action { get; set; } = "config";

    /// <summary>A short human-readable summary of what changed (the changed keys for a
    /// config edit, the disk + size for a grow, the disk + target storage for a move),
    /// denormalised for the audit view.</summary>
    [MaxLength(512)]
    public string? Summary { get; set; }

    /// <summary>Whether Proxmox accepted the change. <c>false</c> when the host
    /// rejected it; the reason is on <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Host error message when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
