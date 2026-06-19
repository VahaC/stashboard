namespace Stashboard.Core.Entities;

/// <summary>
/// V7.9 — a many-to-many link between a <see cref="WebResourceEntity"/> (service)
/// and a Proxmox guest (LXC / VM). The guest is referenced by its stable
/// <c>(ProxmoxConnectionId, VmId)</c> natural key — <b>not</b> a FK to the
/// ephemeral <see cref="ProxmoxGuestEntity.Id"/> — so the link survives the guest
/// row being re-discovered by a scan and round-trips through backup.
/// <para>
/// This is a link, not ownership: the guest stays auto-discovered and owned by
/// its connection. Deleting the service cascades its links away; deleting the
/// Proxmox connection cascades the links to its guests away; the guest rows
/// themselves are untouched (mirrors the <see cref="DockerWatchEntity.WebResourceId"/>
/// detach-don't-delete rule). One link per
/// <c>(WebResourceId, ProxmoxConnectionId, VmId)</c>.
/// </para>
/// </summary>
public class WebResourceProxmoxGuestLinkEntity : BaseEntity
{
    /// <summary>The linked service. Required — a link has no meaning without it.</summary>
    public Guid WebResourceId { get; set; }
    public WebResourceEntity WebResource { get; set; } = default!;

    /// <summary>The Proxmox host the guest lives on. Required.</summary>
    public Guid ProxmoxConnectionId { get; set; }

    /// <summary>The guest's Proxmox numeric id (LXC or QEMU vmid). The node row
    /// (<c>VmId == 0</c>) is never linked — service links are guests only.</summary>
    public int VmId { get; set; }
}
