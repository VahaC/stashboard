using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.13.1 — audit row for a single LXC <strong>create</strong>
/// (<c>POST /nodes/{node}/lxc</c>). One row is written for every attempt that
/// clears the gates and reaches the Proxmox API — whether it succeeds or the host
/// rejects it — so provisioning a container is fully traceable (who, when, which
/// host / node / vmid / hostname / template, and the result). Mirrors
/// <see cref="ProxmoxDestroyAuditEntity"/>: a single instantaneous action, so it
/// records a <see cref="CreatedAtUtc"/> instant plus a success flag rather than
/// start/end timestamps.
/// <para>
/// Like the other Proxmox audit rows the host / guest details are denormalised
/// onto the row (captured at write time) so the history survives a host rename or
/// delete; the connection FK is nullable + <c>SetNull</c> on delete.
/// </para>
/// </summary>
public class ProxmoxCreateAuditEntity : AuditableEntity
{
    /// <summary>User who triggered the create.</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>The Proxmox host the guest belongs to. Nullable + <c>SetNull</c>
    /// on delete so deleting a host keeps the historical rows around.</summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at create time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at create time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>VMID requested for the new LXC.</summary>
    public int VmId { get; set; }

    /// <summary>Hostname requested for the new LXC, for the audit view.</summary>
    [MaxLength(255)]
    public string? Hostname { get; set; }

    /// <summary>Template volume id the container was created from.</summary>
    [MaxLength(512)]
    public string? Template { get; set; }

    /// <summary>Whether Proxmox accepted the create. <c>false</c> when the host
    /// rejected it; the reason is on <see cref="Error"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Host error message when <see cref="Success"/> is <c>false</c>;
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
