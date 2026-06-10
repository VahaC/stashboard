using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V6.11 — public update-check webhook receiver, the Proxmox analogue of
/// <see cref="DockerWebhooksController"/>. An external system (a CI job, a cron,
/// a package-mirror hook) POSTs here to kick off an immediate host scan, bypassing
/// the schedule. The token in the URL is the sole authentication — no JWT, no CSRF
/// — so downstream triggers have zero friction; rotating it in the owner UI
/// invalidates the old URL immediately. Off by default: a host has no token until
/// its owner explicitly creates one.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/proxmox/webhooks")]
public class ProxmoxWebhooksController(
    ApplicationDbContext db,
    IProxmoxScanQueue scanQueue,
    ILogger<ProxmoxWebhooksController> logger) : ControllerBase
{
    [HttpPost("{token}")]
    public async Task<IActionResult> Receive(string token, CancellationToken cancellationToken)
    {
        // Reject anything that isn't the exact 64-char hex shape the generator
        // produces before touching the database — obvious scanner / garbage hits.
        if (!IsWellFormedToken(token)) return NotFound();

        var connection = await db.ProxmoxConnections.AsTracking()
            .FirstOrDefaultAsync(c => c.WebhookToken == token, cancellationToken);
        if (connection is null) return NotFound();

        connection.LastWebhookReceivedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (!connection.Enabled)
        {
            // Accept so the caller doesn't keep retrying, but don't scan — the
            // host is paused. The received timestamp above still records traffic.
            logger.LogInformation(
                "Proxmox webhook accepted but host {ConnectionId} is disabled — not scanning.", connection.Id);
            return Accepted();
        }

        // Best-effort enqueue. Queue saturation still returns 202 — the scheduled
        // scan is the safety net and we don't want the caller hammering us.
        if (!scanQueue.TryEnqueue(connection.Id))
        {
            logger.LogWarning(
                "Proxmox scan queue at capacity; host {ConnectionId} will be picked up by the next scheduled scan.",
                connection.Id);
        }

        return Accepted();
    }

    /// <summary>32-byte hex = 64 lowercase characters. Reject anything else before
    /// hitting the database.</summary>
    private static bool IsWellFormedToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return false;
        foreach (var ch in token)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
        }
        return true;
    }
}
