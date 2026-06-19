using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V7.8 — a per-guest icon override for a Proxmox LXC / VM card, the Proxmox
/// analogue of <see cref="ContainerIconEntity"/>. Keyed by
/// <c>(UserId, ProxmoxConnectionId, VmId)</c>: the vmid is stable on a host, so
/// the chosen icon survives a re-discovery. Only
/// <see cref="ContainerIconSource.Custom"/> rows carry content — deleting the row
/// reverts the card to the auto (OS) / placeholder treatment.
/// </summary>
public class ProxmoxGuestIconEntity : AuditableEntity
{
    public Guid UserId { get; set; }

    /// <summary>The Proxmox connection the guest lives on.</summary>
    public Guid ProxmoxConnectionId { get; set; }

    /// <summary>Proxmox numeric guest id.</summary>
    public int VmId { get; set; }

    public ContainerIconSource IconSource { get; set; } = ContainerIconSource.Auto;

    /// <summary>Base64 data URI of the uploaded image; null for Auto rows.</summary>
    public string? LogoBase64 { get; set; }

    /// <summary>Path under <c>/uploads/container-icons/</c> of the uploaded file;
    /// null for Auto rows.</summary>
    [MaxLength(500)]
    public string? CustomLogoPath { get; set; }
}
