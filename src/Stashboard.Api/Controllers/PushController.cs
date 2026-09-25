using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Notifications.Push;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.6 — web-push subscription management and the public VAPID key. A user
/// subscribes each device once; the same status/update/alert notifications the
/// other channels send are fanned out to those devices. The channel is "on" for a
/// user precisely when they have at least one subscription — there is no separate
/// enable flag. Subscriptions are device-bound bearer material, so they are never
/// exported by backup/restore.
/// </summary>
[ApiController]
[Authorize]
[Route("api/push")]
public class PushController(
    IPushSubscriptionService subscriptions,
    IOptions<VapidOptions> vapid) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    /// <summary>The application-server public key the browser passes to <c>pushManager.subscribe</c>.</summary>
    [HttpGet("vapid-public-key")]
    public ActionResult<VapidPublicKeyResponse> GetVapidPublicKey()
    {
        var publicKey = vapid.Value.PublicKey;
        if (string.IsNullOrWhiteSpace(publicKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Web push is not configured on this server." });
        return Ok(new VapidPublicKeyResponse(publicKey));
    }

    [HttpGet("subscriptions")]
    public async Task<ActionResult<List<PushDeviceResponse>>> ListSubscriptions(CancellationToken cancellationToken)
    {
        var rows = await subscriptions.ListAsync(UserId, cancellationToken);
        return Ok(rows.Select(r => new PushDeviceResponse(r.Id, r.Label, r.CreatedUtc, r.LastSuccessUtc)).ToList());
    }

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.Keys?.P256dh)
            || string.IsNullOrWhiteSpace(request.Keys?.Auth))
            return BadRequest(new { error = "A push subscription needs an endpoint and both keys." });

        var label = DeviceLabel.FromUserAgent(Request.Headers.UserAgent.ToString());
        await subscriptions.UpsertAsync(UserId, request.Endpoint, request.Keys.P256dh, request.Keys.Auth, label, cancellationToken);
        return NoContent();
    }

    [HttpDelete("subscriptions/{id:guid}")]
    public async Task<IActionResult> Unsubscribe(Guid id, CancellationToken cancellationToken)
    {
        var removed = await subscriptions.DeleteAsync(UserId, id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>Sends a test push to every device the user has subscribed.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<PushTestResponse>> SendTest(CancellationToken cancellationToken)
    {
        var result = await subscriptions.SendToUserAsync(
            UserId,
            new PushMessage("Stashboard", "This is a test push notification.", "/", "push-test"),
            cancellationToken);
        return Ok(new PushTestResponse(result.Attempted, result.Delivered, result.Pruned));
    }
}
