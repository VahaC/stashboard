using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V8.1 — audit row for a single LXC <strong>restore</strong> from a vzdump backup
/// archive (<c>POST /nodes/{node}/lxc</c> with <c>restore=1</c>). One row is written
/// for every attempt that clears the gates and reaches the Proxmox API — whether it
/// succeeds or the host rejects it — so the disaster-recovery path is fully traceable
/// (who, when, which host / node / target vmid, which backup, whether it overwrote an
/// existing container, and the result). Mirrors <see cref="ProxmoxCreateAuditEntity"/>:
/// a single instantaneous action, so it records a <see cref="CreatedAtUtc"/> instant
/// plus a success flag rather than start/end timestamps.
/// <para>
/// Like the other Proxmox audit rows the host details are denormalised onto the row
/// (captured at write time) so the history survives a host rename or delete; the
/// connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxRestoreAuditEntity : AuditableEntity
{
    /// <summary>User who triggered the restore.</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>The Proxmox host the guest belongs to. Nullable + <c>SetNull</c>
    /// on delete so deleting a host keeps the historical rows around.</summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at restore time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at restore time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>Target VMID the archive was restored into.</summary>
    public int VmId { get; set; }

    /// <summary>The backup archive volid the container was restored from.</summary>
    [MaxLength(512)]
    public string? BackupVolid { get; set; }

    /// <summary>Whether the restore overwrote an existing container
    /// (<c>force=1</c>) rather than creating a new vmid.</summary>
    public bool Overwrote { get; set; }

    /// <summary>Whether Proxmox accepted the restore. <c>false</c> when the host
    /// rejected it; the reason is on <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Host error message when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
