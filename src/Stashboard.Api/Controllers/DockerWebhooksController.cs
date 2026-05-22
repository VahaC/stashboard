using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V2.6 — public webhook receiver. Registries (Docker Hub, GHCR, generic
/// OCI) POST here whenever a tag is pushed for an image the user has
/// opted in to webhook delivery for. The token in the URL is the sole
/// authentication mechanism — no JWT, no CSRF, no rate limit on the route
/// itself because we want zero friction for downstream registries.
/// </summary>
/// <remarks>
/// Why anonymous: registry webhooks have no JWT, can't perform OAuth,
/// and can rarely set custom headers reliably. The 32-byte CSPRNG token
/// in the URL is the same auth model as GitHub's webhook secret URL,
/// Sentry's release endpoints, etc. The token rotates on demand from the
/// owner UI and old URLs become 404 immediately.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/docker/webhooks")]
public class DockerWebhooksController(
    ApplicationDbContext db,
    IDockerWebhookCheckQueue queue,
    IDockerWebhookPayloadParser payloadParser,
    ILogger<DockerWebhooksController> logger) : ControllerBase
{
    [HttpPost("{watchToken}")]
    public async Task<IActionResult> Receive(string watchToken, CancellationToken cancellationToken)
    {
        // Sanity-check the token shape before touching the database — a 64-char
        // hex string is the exact format Generate() produces, so anything else
        // is an obvious garbage hit (scanner / accidental browser request /
        // misconfigured registry).
        if (!IsWellFormedToken(watchToken)) return NotFound();

        // Read the body up to a sane limit. We don't actually need the payload
        // for the check itself — the URL token already pinpoints the watch —
        // but parsing it for the audit timestamp is useful diagnostics.
        var body = await ReadBodyAsync(Request, cancellationToken);
        var parsed = payloadParser.Parse(body);

        // Single equality lookup on the unique index. Constant-time string
        // comparison isn't worth the complexity here — the token is opaque to
        // an attacker and a timing oracle on its existence yields nothing
        // they couldn't learn from running through their guesses anyway.
        var watch = await db.DockerWatches.AsTracking()
            .FirstOrDefaultAsync(w => w.WebhookToken == watchToken, cancellationToken);
        if (watch is null) return NotFound();

        if (!watch.Enabled)
        {
            // Accept the call so the upstream registry doesn't keep retrying,
            // but don't schedule a check — the watch is paused. Stamp the
            // received timestamp so the diagnostics still show traffic.
            watch.LastWebhookReceivedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Webhook accepted but watch {WatchId} is disabled (source={Source}, repo={Repo}, tag={Tag})",
                watch.Id, parsed.Source, parsed.Repository, parsed.Tag);
            return Accepted();
        }

        watch.LastWebhookReceivedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Best-effort enqueue. Capacity overflow is logged but still 202 — the
        // scheduled scan is the safety net and we don't want the registry to
        // hammer us with retries on a transient queue saturation.
        if (!queue.TryEnqueue(watch.Id))
        {
            logger.LogWarning(
                "Webhook queue at capacity; watch {WatchId} will be picked up by the next scheduled scan (source={Source})",
                watch.Id, parsed.Source);
        }

        return Accepted();
    }

    /// <summary>32-byte hex = 64 lowercase characters. Reject anything else
    /// before hitting the database.</summary>
    private static bool IsWellFormedToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return false;
        foreach (var ch in token)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>Cap inbound bodies at 64 KB. Real registry payloads are a few
    /// hundred bytes; anything bigger is either a misconfiguration or hostile.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Body is null) return null;
        const int maxBytes = 64 * 1024;
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var buffer = new char[maxBytes];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, maxBytes), cancellationToken);
        return read == 0 ? null : new string(buffer, 0, read);
    }
}
