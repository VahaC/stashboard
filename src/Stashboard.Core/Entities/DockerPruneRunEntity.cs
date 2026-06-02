using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V5.5 — audit row for a single image-prune run on a Docker host. Records
/// who or what triggered it, how much disk was freed, and any error so the
/// storage widget can show recent activity ("freed 4.2 GiB on 2026-05-28")
/// and the operator can see when a scheduled run last fired or failed.
/// </summary>
public class DockerPruneRunEntity : AuditableEntity
{
    /// <summary>Connection the prune ran against. <c>SetNull</c> on delete so
    /// the audit row survives the connection being removed.</summary>
    public Guid? DockerConnectionId { get; set; }

    /// <summary>User who initiated a manual run. <c>null</c> for scheduled runs.</summary>
    public Guid? InitiatedByUserId { get; set; }

    public DockerPruneTrigger Trigger { get; set; }

    public DockerPruneStatus Status { get; set; }

    /// <summary>Whether the run included unused (non-dangling) images.</summary>
    public bool IncludedUnused { get; set; }

    /// <summary>Number of images the daemon deleted on this run.</summary>
    public int ImagesDeleted { get; set; }

    /// <summary>Bytes the daemon reports reclaimed.</summary>
    public long SpaceReclaimedBytes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}
