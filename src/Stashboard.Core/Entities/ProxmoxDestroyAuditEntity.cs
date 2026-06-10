using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.13 — audit row for a single LXC <strong>destroy</strong> (the irreversible
/// <c>DELETE /nodes/{node}/lxc/{vmid}</c>). One row is written for every attempt
/// that clears the gates and reaches the Proxmox API — whether it succeeds or the
/// host rejects it — so destroying a container is fully traceable (who, when,
/// which host / node / guest, and the result). Mirrors
/// <see cref="ProxmoxMonitoringAuditEntity"/>: a single instantaneous action, not
/// a streaming session, so it records a <see cref="DestroyedUtc"/> instant plus a
/// success flag rather than start/end timestamps.
/// <para>
/// Like the other Proxmox audit rows the host / guest details are denormalised
/// onto the row (captured at write time) so the history survives a host rename or
/// delete; the connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxDestroyAuditEntity : AuditableEntity
{
    /// <summary>User who triggered the destroy. Denormalised so the audit view
    /// never has to walk back through the connection (and so the row survives a
    /// connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>The Proxmox host the guest belonged to. Nullable + <c>SetNull</c>
    /// on delete so deleting a host keeps the historical rows around.</summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at destroy time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at destroy time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>VMID of the destroyed LXC.</summary>
    public int VmId { get; set; }

    /// <summary>Guest name captured at destroy time, for the audit view.</summary>
    [MaxLength(255)]
    public string? GuestName { get; set; }

    /// <summary>Whether Proxmox accepted the destroy. <c>false</c> when the host
    /// rejected it (e.g. the guest turned out to still be running, or a
    /// permission error); the reason is on <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Host error message when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    public DateTime DestroyedUtc { get; set; } = DateTime.UtcNow;
}
