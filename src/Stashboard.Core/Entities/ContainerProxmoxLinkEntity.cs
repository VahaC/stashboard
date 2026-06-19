using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V7.9 — records the Proxmox guest a Docker container physically runs inside
/// (the common homelab case — Docker living in an LXC or VM). Keyed by
/// <c>(UserId, DockerConnectionId, ContainerName)</c>, the exact shape
/// <see cref="ContainerIconEntity"/> uses: the container name is the stable key
/// (the id is ephemeral), so the link survives a recreate and works for <b>any</b>
/// container, watched or not. The target guest is referenced by its stable
/// <c>(ProxmoxConnectionId, VmId)</c> natural key so it round-trips through backup
/// and survives guest re-discovery.
/// </summary>
public class ContainerProxmoxLinkEntity : AuditableEntity
{
    public Guid UserId { get; set; }

    /// <summary>The Docker connection the container lives on.</summary>
    public Guid DockerConnectionId { get; set; }

    [Required, MaxLength(255)]
    public string ContainerName { get; set; } = default!;

    /// <summary>The Proxmox host the target guest lives on.</summary>
    public Guid ProxmoxConnectionId { get; set; }

    /// <summary>The target guest's vmid (LXC or QEMU).</summary>
    public int VmId { get; set; }
}
