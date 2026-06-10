using Microsoft.Extensions.Options;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — in-memory implementation of <see cref="IProxmoxConsoleSessionRegistry"/>.
/// A single lock guards two small counters (per user, per host); contention is
/// negligible because acquire/release happen only at session start/end. Mirrors
/// the V5.7 container-exec registry.
/// </summary>
public sealed class ProxmoxConsoleSessionRegistry(IOptions<ProxmoxConsoleOptions> options) : IProxmoxConsoleSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _perUser = new();
    private readonly Dictionary<Guid, int> _perHost = new();

    public IDisposable? TryAcquire(Guid userId, Guid connectionId, out string? rejection)
    {
        var maxUser = Math.Max(1, options.Value.MaxSessionsPerUser);
        var maxHost = Math.Max(1, options.Value.MaxSessionsPerHost);

        lock (_gate)
        {
            var userCount = _perUser.GetValueOrDefault(userId);
            if (userCount >= maxUser)
            {
                rejection = $"You already have {userCount} console session(s) open (limit {maxUser}).";
                return null;
            }

            var hostCount = _perHost.GetValueOrDefault(connectionId);
            if (hostCount >= maxHost)
            {
                rejection = $"This host already has {hostCount} console session(s) open (limit {maxHost}).";
                return null;
            }

            _perUser[userId] = userCount + 1;
            _perHost[connectionId] = hostCount + 1;
            rejection = null;
            return new Lease(this, userId, connectionId);
        }
    }

    private void Release(Guid userId, Guid connectionId)
    {
        lock (_gate)
        {
            if (_perUser.TryGetValue(userId, out var u))
            {
                if (u <= 1) _perUser.Remove(userId);
                else _perUser[userId] = u - 1;
            }
            if (_perHost.TryGetValue(connectionId, out var h))
            {
                if (h <= 1) _perHost.Remove(connectionId);
                else _perHost[connectionId] = h - 1;
            }
        }
    }

    private sealed class Lease(ProxmoxConsoleSessionRegistry owner, Guid userId, Guid connectionId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Release(userId, connectionId);
        }
    }
}
