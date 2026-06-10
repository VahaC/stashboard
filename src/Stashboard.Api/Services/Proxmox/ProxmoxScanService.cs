using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V6.0 — the per-connection unit of work for a Proxmox scan: run the checker,
/// upsert the discovered node + LXC guest rows, stamp the connection, and fire
/// a notification when new updates appear. Shared by the timed background loop
/// (<see cref="ProxmoxUpdateBackgroundService"/>) and the manual "Check now"
/// endpoint so the two paths can never drift. Scoped — it persists through the
/// caller's <c>ApplicationDbContext</c>.
/// </summary>
public interface IProxmoxScanService
{
    /// <summary>
    /// Checks one connection and persists the outcome (guest rows + connection
    /// stamp + notification throttle). The <paramref name="connection"/> must
    /// be tracked by this scope's <c>ApplicationDbContext</c>.
    /// </summary>
    Task CheckConnectionAsync(ProxmoxConnectionEntity connection, CancellationToken cancellationToken = default);
}

public sealed class ProxmoxScanService(
    ApplicationDbContext db,
    IProxmoxUpdateChecker checker,
    IProxmoxConnectionMapper mapper,
    IProxmoxUpdateNotificationService notifier,
    IUserService userService,
    ILogger<ProxmoxScanService> logger) : IProxmoxScanService
{
    public async Task CheckConnectionAsync(ProxmoxConnectionEntity connection, CancellationToken cancellationToken = default)
    {
        ProxmoxCheckResult result;
        try
        {
            // V6.11 — a maintenance-window snooze that has elapsed auto-re-enables
            // the guest: clear it before computing the skip set so the very next
            // scan (this one) checks it again. Runs straight against the store so
            // it doesn't fight the change tracker the upsert uses below.
            var now = DateTime.UtcNow;
            await db.ProxmoxGuests
                .Where(g => g.ProxmoxConnectionId == connection.Id
                    && g.MonitoringSnoozedUntil != null && g.MonitoringSnoozedUntil <= now)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(g => g.MonitoringSnoozedUntil, (DateTime?)null), cancellationToken);

            // V6.7 / V6.11 — LXCs the user turned monitoring off for, or that are
            // still inside an active snooze window, are skipped by the checker (no
            // per-guest update count). Read the set up front so the checker can
            // short-circuit them; the node row is never disabled or snoozed.
            var skipVmIds = await db.ProxmoxGuests.AsNoTracking()
                .Where(g => g.ProxmoxConnectionId == connection.Id
                    && (!g.MonitoringEnabled
                        || (g.MonitoringSnoozedUntil != null && g.MonitoringSnoozedUntil > now)))
                .Select(g => g.VmId)
                .ToListAsync(cancellationToken);

            var profile = mapper.BuildProfile(connection);
            result = await checker.CheckAsync(profile, skipVmIds.ToHashSet(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proxmox check failed for connection {ConnectionId}", connection.Id);
            connection.LastCheckedUtc = DateTime.UtcNow;
            connection.LastError = ex.Message;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        connection.LastCheckedUtc = result.CheckedAtUtc;
        connection.LastError = result.Error;

        // On a connection-level failure (API unreachable / auth) the guest list
        // is empty and untrustworthy — keep the last-known cards rather than
        // wiping them, and skip the notification.
        if (result.Error is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var currentGuests = await UpsertGuestsAsync(connection, result, cancellationToken);

        var user = await userService.FindByIdAsync(connection.UserId, cancellationToken);
        if (user is not null)
            await notifier.NotifyIfNeededAsync(user, connection, currentGuests, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<ProxmoxGuestEntity>> UpsertGuestsAsync(
        ProxmoxConnectionEntity connection, ProxmoxCheckResult result, CancellationToken cancellationToken)
    {
        var existing = await db.ProxmoxGuests.AsTracking()
            .Where(g => g.ProxmoxConnectionId == connection.Id)
            .ToListAsync(cancellationToken);
        var byVmId = existing.ToDictionary(g => g.VmId);

        var current = new List<ProxmoxGuestEntity>(result.Guests.Count);
        var seen = new HashSet<int>();

        foreach (var gr in result.Guests)
        {
            seen.Add(gr.VmId);
            if (!byVmId.TryGetValue(gr.VmId, out var entity))
            {
                entity = new ProxmoxGuestEntity
                {
                    Id = Guid.NewGuid(),
                    ProxmoxConnectionId = connection.Id,
                    VmId = gr.VmId,
                };
                db.ProxmoxGuests.Add(entity);
            }

            entity.Name = gr.Name;
            entity.GuestType = gr.GuestType;
            entity.IsRunning = gr.IsRunning;
            entity.PendingUpdates = gr.PendingUpdates;
            entity.LastError = gr.Error;
            entity.IpAddress = gr.IpAddress;
            entity.UptimeSeconds = gr.UptimeSeconds;
            entity.CpuCores = gr.CpuCores;
            entity.MemoryBytes = gr.MemoryBytes;
            entity.DiskBytes = gr.DiskBytes;
            entity.Tags = gr.Tags;
            entity.LastCheckedUtc = result.CheckedAtUtc;
            entity.UpdatedUtc = DateTime.UtcNow;
            current.Add(entity);
        }

        // Drop guests that disappeared from the host (LXC deleted) so the
        // dashboard doesn't show phantom cards.
        foreach (var stale in existing.Where(g => !seen.Contains(g.VmId)))
            db.ProxmoxGuests.Remove(stale);

        return current;
    }
}
