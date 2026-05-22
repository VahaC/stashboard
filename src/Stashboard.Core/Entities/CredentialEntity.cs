using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

public class CredentialEntity : BaseEntity
{
    public Guid WebResourceId { get; set; }
    public WebResourceEntity WebResource { get; set; } = default!;

    [Required, MaxLength(100)]
    public string Key { get; set; } = default!;

    [Required]
    public string EncryptedValue { get; set; } = default!;

    public bool IsSecret { get; set; } = true;
}
