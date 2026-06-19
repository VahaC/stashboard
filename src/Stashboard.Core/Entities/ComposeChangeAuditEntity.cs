using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V7.6 — metadata-only audit row for one change made through the Compose
/// diff-and-apply flow: a Raw-YAML save, a Restore of a kept revision, or an
/// "Apply now" recreate. No file content is stored (that lives in
/// <c>&lt;project&gt;/.stashboard/history/</c>); the row records who changed which
/// project, when, which services were touched, and — for Apply — whether it
/// succeeded. Surfaced read-only on the Audit page's <em>Compose changes</em> tab.
/// </summary>
public class ComposeChangeAuditEntity : AuditableEntity
{
    /// <summary>The Docker connection the change was made against. SetNull on
    /// connection delete so the history survives — the connection name is
    /// denormalised onto <see cref="ConnectionName"/> for exactly that reason.</summary>
    public Guid? DockerConnectionId { get; set; }

    /// <summary>Connection name captured at change time so the row reads sensibly
    /// after the connection is renamed or deleted.</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>The discovered Compose project name (the container label).</summary>
    [Required, MaxLength(200)]
    public string ComposeProject { get; set; } = default!;

    /// <summary>The Compose file that was changed (e.g. <c>docker-compose.yml</c>).</summary>
    [MaxLength(255)]
    public string? FileName { get; set; }

    public ComposeChangeType ChangeType { get; set; }

    /// <summary>Comma-separated service keys the change touched (for Save /
    /// Restore: the services whose definition differed; for Apply: the services
    /// the recreate targeted). <c>null</c> / empty means "the whole project".</summary>
    public string? ChangedServices { get; set; }

    /// <summary>Outcome. Always <c>true</c> for Save / Restore rows (they are only
    /// written after a validated, successful save). For Apply it reflects the
    /// <c>docker compose up -d</c> exit.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Failure detail for an unsuccessful Apply; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }

    /// <summary>User who made the change. Denormalised so the history view never
    /// has to walk back through the connection to find the owner, and survives a
    /// connection delete.</summary>
    public Guid InitiatedByUserId { get; set; }

    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
}
