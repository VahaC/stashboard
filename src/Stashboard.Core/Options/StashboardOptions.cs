namespace Stashboard.Core.Options;

/// <summary>
/// Top-level app feature flags. Bound from the <c>Stashboard</c> config
/// section (or <c>STASHBOARD_Stashboard__*</c> environment variables) at
/// startup. Defaults are deliberately conservative — destructive features
/// stay off unless the operator opts in.
/// </summary>
public sealed class StashboardOptions
{
    public const string SectionName = "Stashboard";

    /// <summary>
    /// V3.5 — when <c>true</c>, the Docker instances page renders the
    /// "Remove container" action and the
    /// <c>DELETE /api/docker/connections/{id}/containers/{name}</c>
    /// endpoint executes the daemon call. Default is <c>false</c>:
    /// removing a container is irreversible from the UI (the
    /// container's writable layer is gone), so it has to be turned on
    /// deliberately by the operator. The UI still gates the action
    /// behind a second confirmation when the flag is enabled.
    /// </summary>
    public bool AllowContainerRemoval { get; set; } = false;
}
