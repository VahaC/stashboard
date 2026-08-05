namespace Stashboard.Api.Notifications;

/// <summary>Apprise notification severity, mapped to the Apprise API <c>type</c> field.</summary>
public enum AppriseNotificationType
{
    Info,
    Success,
    Warning,
    Failure,
}

/// <summary>
/// Posts a notification to an Apprise API endpoint, which fans it out to every
/// configured target (Discord, ntfy, Gotify, Slack, generic webhooks, …) in a
/// single HTTP POST. A non-success HTTP status throws, so callers stamp their
/// per-channel throttle key only after a successful send (mirrors
/// <see cref="ITelegramSender"/>).
/// </summary>
public interface IAppriseSender
{
    /// <param name="baseUrl">Apprise API base URL — root or full <c>/notify</c> endpoint.</param>
    /// <param name="urls">One or more Apprise URLs to deliver to.</param>
    Task SendAsync(
        string baseUrl,
        IReadOnlyList<string> urls,
        string title,
        string body,
        AppriseNotificationType type,
        CancellationToken cancellationToken = default);
}
