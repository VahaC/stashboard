namespace Stashboard.Core.Enums;

/// <summary>V5.5 — terminal outcome of a single prune run, persisted on
/// <c>DockerPruneRunEntity</c>.</summary>
public enum DockerPruneStatus
{
    Success = 0,
    /// <summary>Run finished but reclaimed nothing — kept distinct from
    /// <see cref="Success"/> so the storage widget can downplay it.</summary>
    NothingToPrune = 1,
    HostUnreachable = 2,
    Failed = 3,
    /// <summary>Connection had <c>AllowImagePrune = false</c> at scheduling
    /// time. Records the skip so the operator can see why a run didn't fire.</summary>
    Skipped = 4,
}
