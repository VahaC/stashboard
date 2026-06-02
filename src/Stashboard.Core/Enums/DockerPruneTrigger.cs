namespace Stashboard.Core.Enums;

/// <summary>V5.5 — what kicked off a prune run.</summary>
public enum DockerPruneTrigger
{
    /// <summary>Scheduled background sweep.</summary>
    Scheduled = 0,
    /// <summary>"Prune now" button on the V3.5 instances page.</summary>
    Manual = 1,
}
