using System.Collections.Concurrent;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V2.6 — bounded, lock-free in-memory queue used by the webhook receiver
/// to ask the background loop for an immediate check. Capacity is bounded
/// so a runaway / malicious caller can't grow the queue without bound;
/// duplicate ids inside the queue are collapsed so 50 webhooks for the same
/// watch only produce one check at most per drain cycle.
/// </summary>
public sealed class DockerWebhookCheckQueue : IDockerWebhookCheckQueue
{
    /// <summary>Upper bound for the pending queue. Beyond this point the
    /// webhook controller still returns 202 (delivery is best-effort) and
    /// the scheduled scan is the safety net.</summary>
    public const int Capacity = 1024;

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public bool TryEnqueue(Guid watchId)
    {
        if (watchId == Guid.Empty) return false;
        if (_pending.Count >= Capacity) return false;
        return _pending.TryAdd(watchId, 0);
    }

    public IReadOnlyList<Guid> DrainAll()
    {
        if (_pending.IsEmpty) return Array.Empty<Guid>();
        var drained = _pending.Keys.ToArray();
        foreach (var id in drained) _pending.TryRemove(id, out _);
        return drained;
    }
}
