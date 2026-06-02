using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V5.5 — orchestrator that runs <c>docker image prune</c> for a single
/// connection and persists a <see cref="DockerPruneRunEntity"/> audit row.
/// Wraps the daemon call in the same result-style envelope the rest of the
/// Docker code uses so the controller and background service can branch on
/// status without exception-driven control flow.
/// </summary>
public interface IDockerPruneRunner
{
    Task<DockerPruneRunResult> RunAsync(
        DockerPruneRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>V5.5 — inputs for a single prune run.</summary>
/// <param name="ConnectionId">Connection the run will audit against.</param>
/// <param name="Transport">Resolved transport bundle from <c>IDockerConnectionMapper</c>.</param>
/// <param name="IncludeUnused">When <c>true</c>, also prune images not referenced by any container.</param>
/// <param name="Trigger">Scheduled (background) vs. Manual (UI).</param>
/// <param name="InitiatedByUserId">User id for manual runs; <c>null</c> for scheduled.</param>
public sealed record DockerPruneRequest(
    Guid ConnectionId,
    DockerHostTransport Transport,
    bool IncludeUnused,
    DockerPruneTrigger Trigger,
    Guid? InitiatedByUserId);

/// <summary>V5.5 — outcome surfaced back to the caller. The persisted
/// <see cref="DockerPruneRunEntity"/> mirrors these fields one-for-one.</summary>
public sealed record DockerPruneRunResult(
    DockerPruneStatus Status,
    int ImagesDeleted,
    long SpaceReclaimedBytes,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    bool IncludedUnused,
    string? Error)
{
    public bool IsSuccess => Status is DockerPruneStatus.Success or DockerPruneStatus.NothingToPrune;
}
