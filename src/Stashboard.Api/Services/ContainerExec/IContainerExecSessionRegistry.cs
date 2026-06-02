namespace Stashboard.Api.Services.ContainerExec;

/// <summary>
/// V5.7 — process-wide tally of live container-exec sessions, enforcing the
/// per-user and per-host concurrency caps
/// (<c>Stashboard:ContainerExec:MaxSessionsPerUser</c> /
/// <c>MaxSessionsPerHost</c>). Singleton; a session holds an
/// <see cref="IDisposable"/> lease for its lifetime and releases the slot on
/// dispose. Mirrors the V5.3 host-shell registry.
/// </summary>
public interface IContainerExecSessionRegistry
{
    /// <summary>
    /// Tries to reserve a slot for <paramref name="userId"/> on
    /// <paramref name="connectionId"/>. Returns a lease to dispose when the
    /// session ends, or <c>null</c> when a cap is already reached.
    /// <paramref name="rejection"/> explains which cap blocked it (for the
    /// close message); <c>null</c> on success.
    /// </summary>
    IDisposable? TryAcquire(Guid userId, Guid connectionId, out string? rejection);
}
