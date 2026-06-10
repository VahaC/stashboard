using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Notifications;

/// <summary>
/// V6.8.1 — sends node-health alert notifications (email + Telegram) when a
/// host's active-alert set changes. Reuses the existing channels and the
/// per-channel signature throttle the update notifier established: a steady
/// deviation never re-pings, and a flaky channel doesn't suppress the other.
/// </summary>
public interface IProxmoxNodeAlertNotificationService
{
    /// <summary>
    /// Notifies about the current <paramref name="active"/> alert set when
    /// <paramref name="signature"/> differs from what each channel last sent.
    /// An empty active set after a non-empty one is delivered as an "all clear".
    /// Stamps the matching signature on <paramref name="settings"/> only after a
    /// successful send (the caller persists it).
    /// </summary>
    Task NotifyIfNeededAsync(
        UserEntity user,
        ProxmoxConnectionEntity connection,
        ProxmoxNodeAlertSettingsEntity settings,
        IReadOnlyList<ProxmoxNodeAlertStateEntity> active,
        string signature,
        CancellationToken cancellationToken = default);
}
