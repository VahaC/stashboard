using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

public class CategoryEntity : BaseEntity
{
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    [MaxLength(7)]
    public string Color { get; set; } = "#6c757d";

    public ICollection<WebResourceEntity> Services { get; set; } = new List<WebResourceEntity>();
}
