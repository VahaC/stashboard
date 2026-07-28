namespace Stashboard.Core.Abstractions;

/// <summary>
/// V9.2 — handles the "Stashboard updates itself" case for one-click
/// "Update now". A container cannot reliably recreate itself: the moment it
/// stops / removes its own container, the process performing the recreate dies,
/// so the recreate (create + start) never completes and the container simply
/// vanishes. <see cref="IDockerImageUpdater"/> would break exactly this way when
/// pointed at the Stashboard container itself.
/// </summary>
/// <remarks>
/// The fix is the Watchtower self-update pattern: detect that the target is our
/// own container and offload the pull + recreate to a <em>detached one-shot
/// helper container</em>. The helper runs the same Stashboard image with the
/// <c>self-update</c> command and a serialized <see cref="DockerUpdateProfile"/>
/// in its environment; because it is an independent container it survives the
/// parent's death and runs the proven <see cref="IDockerImageUpdater"/> recreate
/// out of band. It works regardless of transport (local socket, SSH tunnel or
/// TCP): self is decided purely by matching the running container's id against
/// the watch target, which naturally reports "not self" for a container that
/// lives on a genuinely remote daemon.
/// </remarks>
public interface ISelfUpdateLauncher
{
    /// <summary>
    /// Whether <paramref name="profile"/> targets this very Stashboard container.
    /// Never throws — any resolution failure (socket not mounted, host
    /// unreachable, not running in a container) yields <c>false</c> so the caller
    /// falls back to the normal in-process recreate.
    /// </summary>
    Task<bool> IsSelfTargetAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the detached helper that recreates this container out of band.
    /// Returns as soon as the helper has been started (or failed to start) — it
    /// never waits for the recreate, which would kill this process.
    /// </summary>
    Task<SelfUpdateLaunchResult> LaunchAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a bulk "Update project" includes this very Stashboard container as
    /// one of its services. When it does, the whole project recreate must be
    /// offloaded (an in-process `docker compose up -d` would stop the Stashboard
    /// container mid-flight and kill the updater). Never throws.
    /// </summary>
    Task<bool> IsSelfInProjectAsync(DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the detached helper that recreates the whole Compose project out of
    /// band (used when <see cref="IsSelfInProjectAsync"/> is true). Returns as
    /// soon as the helper has started.
    /// </summary>
    Task<SelfUpdateLaunchResult> LaunchProjectAsync(DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of <see cref="ISelfUpdateLauncher.LaunchAsync"/>. <c>Started</c> means
/// the helper container was created and started; the recreate itself happens
/// asynchronously and its final result is observed by the next digest check.
/// </summary>
public sealed record SelfUpdateLaunchResult(bool Started, string? Error);
