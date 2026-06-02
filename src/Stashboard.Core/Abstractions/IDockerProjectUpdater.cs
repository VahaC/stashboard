using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V5.4 — describes one service inside a Compose project that the bulk
/// updater should refresh. <see cref="UpdateProfile"/> is the full
/// credential-aware profile resolved from a tracked
/// <see cref="Stashboard.Core.Entities.DockerWatchEntity"/> when one exists;
/// for untracked containers it's still populated with public defaults so the
/// raw-recreate fallback can attempt an anonymous pull.
/// </summary>
public sealed record DockerProjectServiceTarget(
    string ServiceName,
    string ContainerName,
    DockerUpdateProfile UpdateProfile,
    /// <summary>The user's <c>DockerWatchEntity.Id</c> for this container,
    /// or <c>null</c> if it's not tracked. Used to populate the FK on the
    /// per-service child audit row.</summary>
    Guid? WatchId,
    /// <summary>The watch's linked service (<c>WebResourceId</c>) when one
    /// exists. <c>null</c> for untracked or standalone-tracked containers.</summary>
    Guid? WebResourceId,
    /// <summary>The container's Docker labels at the moment the bulk update
    /// was initiated. Used to read <c>com.docker.compose.depends_on</c> so
    /// the raw-recreate fallback can order services correctly. Empty for
    /// untracked or label-less containers.</summary>
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// V5.4 — input for <see cref="IDockerProjectUpdater.UpdateProjectAsync"/>.
/// </summary>
public sealed record DockerProjectUpdateProfile(
    string ProjectName,
    DockerHostTransport HostTransport,
    /// <summary>V5.2 — when set + local socket + compose CLI present, the
    /// project is recreated via <c>docker compose pull</c> +
    /// <c>docker compose up -d</c> in one shot. Otherwise the updater
    /// falls back to per-service raw recreate in <see cref="Services"/>
    /// order.</summary>
    string? ComposeProjectPath,
    IReadOnlyList<DockerProjectServiceTarget> Services);

/// <summary>
/// V5.4 — outcome of a bulk "Update project" attempt. <see cref="Mode"/>
/// tells the caller which path was taken so the audit row can record it.
/// </summary>
public sealed record DockerProjectUpdateResult(
    DockerProjectUpdateMode Mode,
    DockerUpdateAttemptStatus AggregateStatus,
    string? Error,
    IReadOnlyList<DockerProjectServiceResult> ServiceResults)
{
    public bool IsSuccess => AggregateStatus == DockerUpdateAttemptStatus.Success;
}

/// <summary>
/// V5.4 — outcome for a single service inside the bulk update.
/// </summary>
public sealed record DockerProjectServiceResult(
    string ServiceName,
    string ContainerName,
    Guid? WatchId,
    Guid? WebResourceId,
    string ImageReference,
    DockerUpdateAttemptStatus Status,
    string? PreviousDigest,
    string? NewDigest,
    string? Error,
    bool HealthVerified,
    DateTime? HealthVerifiedUtc);

/// <summary>
/// V5.4 — which dispatch path the updater actually took.
/// </summary>
public enum DockerProjectUpdateMode
{
    /// <summary>One <c>docker compose pull</c> + <c>docker compose up -d</c>
    /// against the project root. Used when the connection is a local socket,
    /// the Compose project path is configured, and the Compose CLI is
    /// available. Honours <c>depends_on</c> ordering and Compose's full
    /// lifecycle.</summary>
    Compose = 0,

    /// <summary>Per-service raw recreate fallback, ordered by the
    /// <c>com.docker.compose.depends_on</c> label graph (best-effort).
    /// Used when Compose isn't available or the project path isn't set.</summary>
    Recreate = 1,
}

/// <summary>
/// V5.4 — bulk update for every service in a Compose project. Mirrors the
/// "never throw" contract of <see cref="IDockerImageUpdater"/>: every failure
/// surfaces as a typed status on the per-service result.
/// </summary>
public interface IDockerProjectUpdater
{
    Task<DockerProjectUpdateResult> UpdateProjectAsync(
        DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default);
}
