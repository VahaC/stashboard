namespace Stashboard.Api.Notifications.Push;

/// <summary>Outcome of a single web-push delivery attempt.</summary>
public enum PushSendOutcome
{
    /// <summary>The push service accepted the message.</summary>
    Delivered,
    /// <summary>The endpoint is gone (HTTP 404/410) — the subscription must be pruned.</summary>
    Expired,
    /// <summary>A transient failure (network, 5xx, misconfiguration) — keep the subscription and retry later.</summary>
    Failed,
}

/// <summary>
/// V10.6 — encrypts and delivers a single web-push message to one subscription,
/// signing it with the app's VAPID key. Distinguishes a permanently-gone endpoint
/// (so the caller can prune it) from a transient failure (so the caller retries).
/// </summary>
public interface IPushSender
{
    Task<PushSendOutcome> SendAsync(
        string endpoint, string p256dh, string auth, string payloadJson, CancellationToken cancellationToken = default);
}
