using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V7.8 — a per-container icon override, mirroring the <c>WebResource</c> logo
/// fields. Keyed by <c>(UserId, DockerConnectionId, ContainerName)</c>: the row
/// is keyed on the container's stable name on a host, not its ephemeral id, so a
/// recreate keeps the chosen icon. Only <see cref="ContainerIconSource.Custom"/>
/// rows are persisted with content — an <see cref="ContainerIconSource.Auto"/>
/// row means "fall back to the official resolver" and is functionally the same
/// as having no row at all (delete reverts to Auto).
/// </summary>
public class ContainerIconEntity : AuditableEntity
{
    public Guid UserId { get; set; }

    /// <summary>The Docker connection the container lives on.</summary>
    public Guid DockerConnectionId { get; set; }

    [Required, MaxLength(255)]
    public string ContainerName { get; set; } = default!;

    public ContainerIconSource IconSource { get; set; } = ContainerIconSource.Auto;

    /// <summary>Base64 data URI of the uploaded image
    /// (<c>data:&lt;mime&gt;;base64,&lt;data&gt;</c>); null for Auto rows.</summary>
    public string? LogoBase64 { get; set; }

    /// <summary>Path under <c>/uploads/container-icons/</c> of the uploaded file;
    /// null for Auto rows. Kept alongside <see cref="LogoBase64"/> exactly as the
    /// WebResource logo path is, so the cleanup guard can delete the file on reset.</summary>
    [MaxLength(500)]
    public string? CustomLogoPath { get; set; }
}
