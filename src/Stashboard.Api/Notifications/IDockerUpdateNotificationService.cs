using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Notifications;

/// <summary>
/// Sends a one-shot email when a Docker watch first observes a newer image
/// digest in its registry. Throttle key is <c>LastNotifiedDigest</c> — the
/// same un-actioned update is never re-emailed, but a subsequent, distinct
/// new digest triggers a fresh email.
/// </summary>
public interface IDockerUpdateNotificationService
{
    /// <summary>
    /// Sends the "update available" email if (a) the watch is in
    /// <c>UpdateAvailable</c> state, (b) <c>UpdateNotificationsEnabled</c>
    /// is true on the watch, and (c) the current <c>LatestDigest</c> is
    /// different from <c>LastNotifiedDigest</c>. On a successful send the
    /// watch's <c>LastNotifiedDigest</c> and <c>LastNotificationSentUtc</c>
    /// are updated in memory — the caller is responsible for
    /// <c>SaveChangesAsync</c>.
    /// </summary>
    /// <param name="service">V3.6 — optional. <c>null</c> for a standalone
    /// tracked container that isn't linked to a service; the notification then
    /// uses the watch's label / container name as the display name.</param>
    Task NotifyIfNeededAsync(
        UserEntity user,
        WebResourceEntity? service,
        DockerWatchEntity watch,
        CancellationToken cancellationToken = default);
}
