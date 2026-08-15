using System.ComponentModel.DataAnnotations;

namespace Stashboard.Core.Entities;

/// <summary>
/// V10.2 — one service selected onto a <see cref="StatusPageEntity"/>. Carries an optional
/// public <see cref="DisplayName"/> so the public view doesn't leak the owner's internal
/// service naming, plus a <see cref="SortOrder"/> for the rendered order. Cascade-deleted with
/// either the page or the underlying service.
/// </summary>
public class StatusPageItemEntity : BaseEntity
{
    public Guid StatusPageId { get; set; }
    public StatusPageEntity? StatusPage { get; set; }

    public Guid WebResourceId { get; set; }
    public WebResourceEntity? WebResource { get; set; }

    /// <summary>Optional public-facing label shown instead of the service's real name. Null =
    /// fall back to the service name.</summary>
    [MaxLength(100)]
    public string? DisplayName { get; set; }

    public int SortOrder { get; set; }
}
