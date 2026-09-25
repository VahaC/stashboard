using System.Net;
using Microsoft.Extensions.Options;
using WebPush;

namespace Stashboard.Api.Notifications.Push;

/// <inheritdoc cref="IPushSender"/>
/// <remarks>
/// Thin wrapper over the <c>WebPush</c> library, which does the VAPID JWT signing
/// and RFC 8291 <c>aes128gcm</c> payload encryption. A 404/410 from the push
/// service means the endpoint is permanently gone, which we surface as
/// <see cref="PushSendOutcome.Expired"/> so the caller prunes the subscription;
/// everything else is a transient <see cref="PushSendOutcome.Failed"/>.
/// </remarks>
public sealed class PushSender(IOptions<VapidOptions> options, ILogger<PushSender> logger) : IPushSender
{
    private readonly WebPushClient _client = new();
    private readonly VapidOptions _vapid = options.Value;

    public async Task<PushSendOutcome> SendAsync(
        string endpoint, string p256dh, string auth, string payloadJson, CancellationToken cancellationToken = default)
    {
        if (!_vapid.IsConfigured)
        {
            logger.LogWarning("Web push send skipped — VAPID keys are not configured.");
            return PushSendOutcome.Failed;
        }

        var subscription = new PushSubscription(endpoint, p256dh, auth);
        var vapidDetails = new VapidDetails(_vapid.Subject, _vapid.PublicKey, _vapid.PrivateKey);

        try
        {
            await _client.SendNotificationAsync(subscription, payloadJson, vapidDetails, cancellationToken);
            return PushSendOutcome.Delivered;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendOutcome.Expired;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web push send failed for endpoint {Endpoint}", endpoint);
            return PushSendOutcome.Failed;
        }
    }
}
