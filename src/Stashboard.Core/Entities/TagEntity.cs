using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

public class TagEntity : BaseEntity
{
    public Guid UserId { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = default!;

    public ICollection<WebResourceTagEntity> WebResourceTags { get; set; } = new List<WebResourceTagEntity>();
}
