namespace Stashboard.Core.Abstractions;

/// <summary>
/// V6.11 — process-local queue of Proxmox connection ids that need an immediate
/// out-of-band scan after an update-check webhook fired. The public webhook
/// controller enqueues (non-blocking, bounded so a malicious caller can't OOM the
/// host); the background loop drains the queue inside its tick and runs the scan,
/// bypassing the schedule. The Proxmox analogue of
/// <see cref="IDockerWebhookCheckQueue"/>.
/// </summary>
/// <remarks>
/// In-memory only — deliberately. The webhook is a latency optimisation; the
/// schedule-driven scan is the source of truth. Dropping one during a restart
/// just means the host waits for its next scheduled tick.
/// </remarks>
public interface IProxmoxScanQueue
{
    /// <summary>
    /// Enqueue a connection id for an immediate scan. Returns <c>true</c> when
    /// accepted, <c>false</c> when the queue is at capacity (the caller still
    /// returns 202 — the scheduled scan is the safety net).
    /// </summary>
    bool TryEnqueue(Guid connectionId);

    /// <summary>Drain every queued id without blocking. The background loop calls
    /// this once per tick.</summary>
    IReadOnlyList<Guid> DrainAll();
}
