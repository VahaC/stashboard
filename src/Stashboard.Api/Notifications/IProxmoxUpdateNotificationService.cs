using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Notifications;

/// <summary>
/// V6.0 — sends a one-shot email (and optional Telegram message) when a
/// Proxmox host's set of pending updates first appears or changes. The throttle
/// key is a signature of the "(vmid:count)" pairs, so the same un-applied
/// updates are never re-sent every tick, but a newly-appeared update fires a
/// fresh notification. Mirrors <see cref="IDockerUpdateNotificationService"/>.
/// </summary>
public interface IProxmoxUpdateNotificationService
{
    /// <summary>
    /// Sends the notification when (a) <c>UpdateNotificationsEnabled</c> is set
    /// on the connection, (b) at least one guest has pending updates, and (c)
    /// the current signature differs from the channel's last-notified
    /// signature. On a successful send the connection's throttle fields are
    /// updated in memory — the caller owns <c>SaveChangesAsync</c>.
    /// </summary>
    Task NotifyIfNeededAsync(
        UserEntity user,
        ProxmoxConnectionEntity connection,
        IReadOnlyList<ProxmoxGuestEntity> guests,
        CancellationToken cancellationToken = default);
}
