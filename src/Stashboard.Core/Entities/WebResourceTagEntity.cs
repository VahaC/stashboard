namespace Stashboard.Core.Entities;

/// <summary>
/// Pure many-to-many join. Composite primary key (WebResourceId, TagId), so it deliberately
/// does NOT inherit from <see cref="BaseEntity"/> — it has no surrogate Id.
/// </summary>
public class WebResourceTagEntity
{
    public Guid WebResourceId { get; set; }
    public WebResourceEntity Service { get; set; } = default!;

    public Guid TagId { get; set; }
    public TagEntity Tag { get; set; } = default!;
}
