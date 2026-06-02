using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V2.7 — audit log row for a single one-click "Update now" attempt
/// against a <see cref="DockerWatchEntity"/>. The row is written
/// regardless of outcome so the user has a complete history in the
/// modal's "Update history" panel and so a failed attempt isn't
/// silently swallowed.
/// </summary>
public class DockerUpdateAttemptEntity : AuditableEntity
{
    /// <summary>
    /// V2.7: required. V3.5: nullable — container actions taken from the
    /// instances page (Start / Stop / Restart / Remove) aren't tied to a
    /// service, so the row stores the connection id instead. Existing
    /// rows back-fill to non-null and continue to point at their parent
    /// service.
    /// </summary>
    public Guid? WebResourceId { get; set; }

    /// <summary>
    /// V2.7: required. V3.5: nullable — container actions taken from the
    /// instances page on a container that isn't tracked by a watch leave
    /// this null. Existing rows back-fill to non-null.
    /// </summary>
    public Guid? DockerWatchId { get; set; }

    /// <summary>
    /// V3.5 — the Docker connection the action was performed against.
    /// Populated for every new row (both Update Now and instance-page
    /// actions). Nullable for back-compat with V2.7 rows that pre-date
    /// the column.
    /// </summary>
    public Guid? DockerConnectionId { get; set; }

    /// <summary>
    /// V3.5 — Docker container id at action time. Captured alongside
    /// <see cref="ContainerName"/> so we can cross-reference if the
    /// container is renamed or recreated under a new id later. <c>null</c>
    /// for actions that failed before the daemon reported an id
    /// (e.g. host unreachable).
    /// </summary>
    [MaxLength(100)]
    public string? ContainerId { get; set; }

    /// <summary>User who clicked the button. Denormalised so the history
    /// view never has to walk back through the watch + service to find
    /// the owner.</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>
    /// V3.5 — what kind of lifecycle action this row represents. Every
    /// row in the table tags itself, so the history view (and any
    /// future per-connection activity log) can filter without joining.
    /// V2.7 rows back-fill to <see cref="DockerContainerActionType.Update"/>.
    /// </summary>
    public DockerContainerActionType ActionType { get; set; } = DockerContainerActionType.Update;

    public DockerUpdateAttemptStatus Status { get; set; }

    /// <summary>Image reference at the moment the user clicked Update — the
    /// reference can change later via the watch form, so the attempt row
    /// captures what was actually pulled.</summary>
    [Required, MaxLength(500)]
    public string ImageReference { get; set; } = default!;

    [Required, MaxLength(200)]
    public string ContainerName { get; set; } = default!;

    /// <summary>Digest the container ran on before the recreate.
    /// <c>null</c> only when the host wasn't reachable / the container
    /// didn't exist — i.e. <see cref="Status"/> is
    /// <see cref="DockerUpdateAttemptStatus.HostUnreachable"/> /
    /// <see cref="DockerUpdateAttemptStatus.ContainerNotFound"/>.</summary>
    [MaxLength(100)]
    public string? PreviousDigest { get; set; }

    /// <summary>Digest the container is running after the recreate.
    /// Populated on <see cref="DockerUpdateAttemptStatus.Success"/>; may
    /// also be filled on <see cref="DockerUpdateAttemptStatus.RecreateFailed"/>
    /// when the pull landed (so we know what *would have* been activated).</summary>
    [MaxLength(100)]
    public string? NewDigest { get; set; }

    /// <summary>Human-readable error message attached to a non-success
    /// status. Mirrors the convention used by the orchestrator —
    /// <c>null</c> on success.</summary>
    public string? Error { get; set; }

    /// <summary>UTC timestamp when the recreate completed (or aborted).
    /// Equals <see cref="AuditableEntity.CreatedUtc"/> for synchronous
    /// attempts; kept as its own field in case future work moves the
    /// pull to a background worker.</summary>
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// V3.2 — true when the recreated container either reported a
    /// <c>healthy</c> Docker healthcheck state, or has no healthcheck
    /// defined (<c>none</c>), within the post-start verification window.
    /// False on every other terminal status (including
    /// <see cref="DockerUpdateAttemptStatus.RecreateFailed"/> downgrades
    /// where the container started but never became healthy). Always false
    /// for attempts that never reached the start step (pull/host/container
    /// failures) — the field is meaningless then.
    /// </summary>
    public bool HealthVerified { get; set; }

    /// <summary>
    /// V3.2 — UTC timestamp at which <see cref="HealthVerified"/> flipped
    /// to <c>true</c>. <c>null</c> for attempts that never reached the
    /// verification step (pull/host/container failures) or that timed out
    /// waiting for the container to become healthy.
    /// </summary>
    public DateTime? HealthVerifiedUtc { get; set; }

    /// <summary>
    /// V5.4 — Compose project name this row belongs to. Populated on every
    /// row that participates in a bulk project update (the aggregate parent
    /// AND each per-service child). <c>null</c> for V2.7 / V3.5 rows that
    /// pre-date this column or for single-service updates.
    /// </summary>
    [MaxLength(200)]
    public string? ComposeProject { get; set; }

    /// <summary>
    /// V5.4 — when set, this row is a child of an aggregate "Update project"
    /// attempt. The parent row carries the aggregate status; child rows
    /// describe what happened to each individual service. <c>null</c> on
    /// every existing row and on the parent itself.
    /// </summary>
    public Guid? ParentAttemptId { get; set; }
}
