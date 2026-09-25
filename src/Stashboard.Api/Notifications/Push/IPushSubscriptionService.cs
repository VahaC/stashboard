using Stashboard.Api.Data;

namespace Stashboard.Api.Notifications.Push;

/// <summary>A ready-to-send web-push notification: the same title/body the other
/// channels use, plus an optional deep-link URL and a tag for OS-level dedup.</summary>
public sealed record PushMessage(string Title, string Body, string? Url = null, string? Tag = null);

/// <summary>Result of fanning one message out to a user's devices.</summary>
/// <param name="Attempted">Live subscriptions the message was sent to.</param>
/// <param name="Delivered">Subscriptions the push service accepted.</param>
/// <param name="Pruned">Subscriptions removed because their endpoint was gone (404/410).</param>
/// <param name="HadTransientFailure">True if any send failed transiently — the caller
/// should leave its throttle key unstamped and retry on the next tick.</param>
public sealed record PushFanoutResult(int Attempted, int Delivered, int Pruned, bool HadTransientFailure);

/// <summary>
/// V10.6 — manages a user's web-push subscriptions and fans notifications out to
/// their devices. The web-push channel is "configured" for a user precisely when
/// they have at least one subscription; there is no separate on/off flag.
/// </summary>
public interface IPushSubscriptionService
{
    Task<IReadOnlyList<PushSubscriptionEntity>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Registers (or refreshes) a device subscription, keyed on its endpoint.</summary>
    Task UpsertAsync(
        Guid userId, string endpoint, string p256dh, string auth, string? label, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the user's subscriptions by id. Returns false when it wasn't theirs.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>True when the user has at least one subscription (the channel gate).</summary>
    Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Sends <paramref name="message"/> to every device the user has subscribed,
    /// pruning any endpoint the push service reports as gone.</summary>
    Task<PushFanoutResult> SendToUserAsync(
        Guid userId, PushMessage message, CancellationToken cancellationToken = default);
}
