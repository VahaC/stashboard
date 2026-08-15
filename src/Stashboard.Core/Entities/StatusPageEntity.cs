using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V10.2 — a user-authored public status page: a named selection of the owner's own
/// services, shareable at a public <see cref="Slug"/> with no account required. The page
/// is private until <see cref="IsPublished"/> is turned on; an unpublished (or unknown)
/// slug 404s publicly, so unpublished pages can't be enumerated.
/// <para>
/// The public read endpoint exposes only display fields (the per-item display name, live
/// status, uptime % and the recent-history bar) — never the underlying service URLs,
/// credentials, notes, categories, tags or Docker/Proxmox internals. The owner's raw
/// service list is only ever reachable through an explicitly published page.
/// </para>
/// </summary>
public class StatusPageEntity : AuditableEntity
{
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Title { get; set; } = default!;

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>Public URL segment (<c>/status/{slug}</c>). Globally unique — the public
    /// lookup has no user context — lowercase kebab-case, validated on write.</summary>
    [Required, MaxLength(80)]
    public string Slug { get; set; } = default!;

    /// <summary>Off by default. While false the page 404s on the public endpoint and is
    /// invisible to anyone but its owner.</summary>
    public bool IsPublished { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The chosen services, each with an optional public display-name override.</summary>
    public ICollection<StatusPageItemEntity> Items { get; set; } = new List<StatusPageItemEntity>();
}
