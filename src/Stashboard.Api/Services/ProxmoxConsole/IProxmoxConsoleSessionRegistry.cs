namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — process-wide tally of live LXC-console sessions, enforcing the
/// per-user and per-host concurrency caps
/// (<c>Stashboard:ProxmoxConsole:MaxSessionsPerUser</c> /
/// <c>MaxSessionsPerHost</c>). Singleton; a session holds an
/// <see cref="IDisposable"/> lease for its lifetime and releases the slot on
/// dispose. Mirrors the V5.7 container-exec registry.
/// </summary>
public interface IProxmoxConsoleSessionRegistry
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
