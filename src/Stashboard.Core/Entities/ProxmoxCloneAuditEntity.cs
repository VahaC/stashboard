using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V8.0 — audit row for a single <strong>clone</strong> or <strong>snapshot</strong>
/// lifecycle action (clone / snapshot create / rollback / delete). One row is
/// written for every attempt that clears the gates and reaches the Proxmox API —
/// whether it succeeds or the host rejects it — so the everyday "new container" and
/// snapshot paths are fully traceable (who, when, which host / node / vmid,
/// <see cref="Action"/>, <see cref="Target"/>, and the result). Mirrors
/// <see cref="ProxmoxCreateAuditEntity"/>: a single instantaneous action, so it
/// records a <see cref="CreatedAtUtc"/> instant plus a success flag rather than
/// start/end timestamps.
/// <para>
/// Like the other Proxmox audit rows the host details are denormalised onto the row
/// (captured at write time) so the history survives a host rename or delete; the
/// connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxCloneAuditEntity : AuditableEntity
{
    /// <summary>User who triggered the action.</summary>
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

    /// <summary>Source guest the action acted on (the cloned-from CT, or the guest
    /// the snapshot belongs to).</summary>
    public int VmId { get; set; }

    /// <summary>Which lifecycle action this row records.</summary>
    public ProxmoxCloneAction Action { get; set; }

    /// <summary>The action's target, denormalised for the audit view: the new vmid
    /// (+ hostname) for a clone, or the snapshot name for a snapshot action.</summary>
    [MaxLength(255)]
    public string? Target { get; set; }

    /// <summary>Whether Proxmox accepted the action. <c>false</c> when the host
    /// rejected it; the reason is on <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Host error message when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
