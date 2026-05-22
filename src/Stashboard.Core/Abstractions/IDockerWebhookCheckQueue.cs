namespace Stashboard.Core.Abstractions;

/// <summary>
/// V2.6 — process-local queue of watch ids that need an immediate re-check
/// after a registry webhook fired. The webhook controller enqueues
/// (non-blocking, bounded so a malicious caller can't OOM the host); the
/// background loop drains the queue inside its tick and runs the check
/// out-of-band, bypassing the schedule.
/// </summary>
/// <remarks>
/// In-memory only — deliberately. Webhooks are a latency optimisation; the
/// schedule-driven scan is the source of truth. Dropping a webhook during a
/// restart just means the watch waits for its next scheduled tick (or for
/// the next webhook). No durability is required.
/// </remarks>
public interface IDockerWebhookCheckQueue
{
    /// <summary>
    /// Enqueue a watch id for an immediate re-check. Returns <c>true</c> when
    /// the id was accepted, <c>false</c> when the queue is at capacity (the
    /// caller still returns 202 — see the controller for why).
    /// </summary>
    bool TryEnqueue(Guid watchId);

    /// <summary>Drain every queued id without blocking. The background loop
    /// calls this once per tick.</summary>
    IReadOnlyList<Guid> DrainAll();
}
