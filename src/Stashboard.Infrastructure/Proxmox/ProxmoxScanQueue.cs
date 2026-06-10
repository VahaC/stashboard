using System.Collections.Concurrent;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V6.11 — bounded, lock-free in-memory queue used by the public update-check
/// webhook receiver to ask the background loop for an immediate scan. Capacity is
/// bounded so a runaway / malicious caller can't grow it without bound; duplicate
/// ids are collapsed so 50 webhooks for the same host only produce one scan per
/// drain cycle. Mirrors <c>DockerWebhookCheckQueue</c>.
/// </summary>
public sealed class ProxmoxScanQueue : IProxmoxScanQueue
{
    /// <summary>Upper bound for the pending queue. Beyond this point the webhook
    /// controller still returns 202 (delivery is best-effort) and the scheduled
    /// scan is the safety net.</summary>
    public const int Capacity = 1024;

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public bool TryEnqueue(Guid connectionId)
    {
        if (connectionId == Guid.Empty) return false;
        if (_pending.Count >= Capacity) return false;
        return _pending.TryAdd(connectionId, 0);
    }

    public IReadOnlyList<Guid> DrainAll()
    {
        if (_pending.IsEmpty) return Array.Empty<Guid>();
        var drained = _pending.Keys.ToArray();
        foreach (var id in drained) _pending.TryRemove(id, out _);
        return drained;
    }
}
