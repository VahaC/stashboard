using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

public class CategoryEntity : BaseEntity
{
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    [MaxLength(7)]
    public string Color { get; set; } = "#6c757d";

    /// <summary>V10.6 — explicit position of this category's group in the dashboard's
    /// Custom sort mode when grouping by category is on. Dense 0..N, rewritten on every
    /// group drag. The "Uncategorized" pseudo-group is not a row and always sorts last.</summary>
    public int SortOrder { get; set; }

    public ICollection<WebResourceEntity> Services { get; set; } = new List<WebResourceEntity>();
}
