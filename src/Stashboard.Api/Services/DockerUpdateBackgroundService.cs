using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Core.Scheduling;

namespace Stashboard.Api.Services;

/// <summary>
/// Periodic scan that runs every <see cref="DockerUpdateOptions.TickIntervalSeconds"/>
/// (default 300 s) and, for each enabled <see cref="DockerWatchEntity"/>
/// whose V2.2 schedule fires (Hourly N-hour roll / Daily at UTC time /
/// Weekly on day + UTC time), calls the orchestrator, persists the result,
/// and triggers a notification when a new <c>LatestDigest</c> is first
/// observed.
/// </summary>
/// <remarks>
/// <para>Watches are processed sequentially to stay friendly to registry rate
/// limits (Docker Hub anonymous = 100 manifest pulls / 6 h).</para>
/// <para>V2.6 — between the schedule-driven sweep and the tick delay the
/// loop also drains <see cref="IDockerWebhookCheckQueue"/>. Webhook-queued
/// watches are processed every <see cref="WebhookDrainIntervalSeconds"/>
/// (default 5 s) so an inbound registry webhook surfaces as
/// "Update available" within seconds of the push, without waiting for the
/// next 5-minute tick.</para>
/// <para><see cref="ScanOnceAsync"/> is public so integration tests can drive
/// a deterministic single pass against the real test database without spinning
/// up the timed loop.</para>
/// </remarks>
public sealed class DockerUpdateBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<DockerUpdateOptions> options,
    IDockerWebhookCheckQueue webhookQueue,
    ILogger<DockerUpdateBackgroundService> logger)
    : BackgroundService
{
    private const int MinimumTickIntervalSeconds = 30;
    private const int StartupDelaySeconds = 10;

    /// <summary>How often the loop wakes up just to drain the webhook queue
    /// in between schedule-driven sweeps. Short so push latency stays low
    /// without paying for a full DB query.</summary>
    private const int WebhookDrainIntervalSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var nextScheduledSweep = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            try
            {
                if (now >= nextScheduledSweep)
                {
                    await ScanOnceAsync(stoppingToken);
                    var interval = Math.Max(MinimumTickIntervalSeconds, options.CurrentValue.TickIntervalSeconds);
                    nextScheduledSweep = now.AddSeconds(interval);
                }
                else
                {
                    await DrainWebhookQueueOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Docker update tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(WebhookDrainIntervalSeconds), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// V2.6 — drains every watch id queued by the webhook receiver and
    /// runs an immediate check on each. Bypasses the V2.2 schedule (the
    /// caller already knows the upstream registry pushed a new image)
    /// but reuses the same orchestrator + notification path so the user
    /// experience is identical to a scheduled tick. Public for tests.
    /// </summary>
    public async Task DrainWebhookQueueOnceAsync(CancellationToken cancellationToken)
    {
        var ids = webhookQueue.DrainAll();
        if (ids.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<IDockerUpdateChecker>();
        var mapper = scope.ServiceProvider.GetRequiredService<IDockerWatchMapper>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDockerUpdateNotificationService>();

        var watches = await db.DockerWatches.AsTracking()
            .Where(w => ids.Contains(w.Id) && w.Enabled)
            .ToListAsync(cancellationToken);
        if (watches.Count == 0) return;

        await ProcessWatchesAsync(db, checker, mapper, userService, notifier, watches, cancellationToken);
    }

    /// <summary>
    /// Runs a single scan pass: picks up due watches, checks each, persists
    /// the result, and notifies on first observation of a new latest digest.
    /// Public so tests can drive a deterministic pass without relying on the
    /// timed loop.
    /// </summary>
    public async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<IDockerUpdateChecker>();
        var mapper = scope.ServiceProvider.GetRequiredService<IDockerWatchMapper>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDockerUpdateNotificationService>();

        // Load all enabled watches, then filter in memory for due-ness — the
        // V2.2 schedule logic (Hourly / Daily / Weekly with UTC-time anchors)
        // doesn't translate cleanly to SQL and the watch count per deployment
        // is small enough that in-memory filtering is cheap.
        var enabled = await db.DockerWatches.AsTracking()
            .Where(w => w.Enabled)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var due = enabled
            .Where(w => CheckScheduleEvaluator.IsDue(
                w.ScheduleType, w.CheckEveryHours, w.CheckAtTime, w.CheckOnDayOfWeek,
                w.LastCheckedUtc, now))
            .ToList();

        if (due.Count == 0) return;

        await ProcessWatchesAsync(db, checker, mapper, userService, notifier, due, cancellationToken);
    }

    /// <summary>
    /// Shared per-watch processing used by both the schedule-driven sweep
    /// and the V2.6 webhook drain. Runs the orchestrator, persists the
    /// result, and notifies on first observation of a new latest digest.
    /// Watches are processed sequentially to stay friendly to registry rate
    /// limits.
    /// </summary>
    private async Task ProcessWatchesAsync(
        ApplicationDbContext db,
        IDockerUpdateChecker checker,
        IDockerWatchMapper mapper,
        IUserService userService,
        IDockerUpdateNotificationService notifier,
        IReadOnlyList<DockerWatchEntity> watches,
        CancellationToken cancellationToken)
    {
        var userIds = watches.Select(watch => watch.UserId).Distinct().ToList();

        // V3.6 — the watch owns its connection id directly, so the host
        // transport is resolved by a straight lookup rather than a join
        // through the (now optional) parent service.
        var connectionIds = watches.Select(w => w.DockerConnectionId).Distinct().ToList();
        var connections = await db.DockerConnections.AsNoTracking()
            .Where(c => connectionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Only watches still linked to a service need the service row (for the
        // notification's display name + offline gating). Standalone containers
        // notify under their own label.
        var serviceIds = watches
            .Where(w => w.WebResourceId is not null)
            .Select(w => w.WebResourceId!.Value)
            .Distinct()
            .ToList();
        var services = serviceIds.Count == 0
            ? new Dictionary<Guid, WebResourceEntity>()
            : await db.WebResources.AsNoTracking()
                .Where(s => serviceIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, cancellationToken);

        foreach (var watch in watches)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // A watch can only run when its owning connection still exists. The
            // connection delete cascades watches away, so this guard mostly
            // covers a connection deleted between the snapshot and the loop.
            if (!connections.TryGetValue(watch.DockerConnectionId, out var connection))
            {
                watch.LastError = "The Docker connection for this container no longer exists.";
                watch.LastCheckedUtc = DateTime.UtcNow;
                watch.UpdateStatus = DockerUpdateStatus.Error;
                continue;
            }

            try
            {
                var profile = mapper.BuildProfileFromEntity(watch, connection);
                var result = await checker.CheckAsync(profile, cancellationToken);
                DockerWatchStatusWriter.ApplyCheckResult(watch, result);

                if (result.Status == DockerUpdateStatus.UpdateAvailable
                    && userIds.Contains(watch.UserId))
                {
                    var user = await userService.FindByIdAsync(watch.UserId, cancellationToken);
                    if (user is not null)
                    {
                        WebResourceEntity? service = null;
                        if (watch.WebResourceId is { } sid)
                            services.TryGetValue(sid, out service);
                        await notifier.NotifyIfNeededAsync(user, service, watch, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Docker update check failed for watch {WatchId}", watch.Id);
                watch.LastError = ex.Message;
                watch.LastCheckedUtc = DateTime.UtcNow;
                watch.UpdateStatus = DockerUpdateStatus.Error;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
