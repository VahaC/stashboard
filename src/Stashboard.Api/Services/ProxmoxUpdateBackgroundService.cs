using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Data;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;
using Stashboard.Core.Scheduling;

namespace Stashboard.Api.Services;

/// <summary>
/// V6.0 — periodic scan that runs every
/// <see cref="ProxmoxUpdateOptions.TickIntervalSeconds"/> (default 300 s) and,
/// for each enabled <c>ProxmoxConnectionEntity</c> whose V2.2 schedule fires,
/// checks the host and persists the result. Mirrors
/// <see cref="DockerUpdateBackgroundService"/>. V6.11 adds a webhook path: in
/// between schedule-driven sweeps the loop drains
/// <see cref="IProxmoxScanQueue"/> and scans any host an inbound update-check
/// webhook asked for, out-of-band.
/// <para><see cref="ScanOnceAsync"/> and
/// <see cref="DrainScanQueueOnceAsync"/> are public so tests can drive a
/// deterministic single pass without the timed loop.</para>
/// </summary>
public sealed class ProxmoxUpdateBackgroundService(
    IServiceScopeFactory scopeFactory,
    IProxmoxScanQueue scanQueue,
    IOptionsMonitor<ProxmoxUpdateOptions> options,
    ILogger<ProxmoxUpdateBackgroundService> logger)
    : BackgroundService
{
    private const int MinimumTickIntervalSeconds = 30;
    private const int StartupDelaySeconds = 15;

    /// <summary>How often the loop wakes up just to drain the webhook scan queue
    /// in between schedule-driven sweeps. Short so webhook latency stays low
    /// without paying for a full schedule evaluation.</summary>
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

                    // V6.8.1 — node-health alerting evaluates on every sweep,
                    // independent of the (often 24 h) per-host update schedule, so
                    // saturation / thermal deviations are caught in minutes and the
                    // debounce window is measured in ticks rather than days.
                    try { await EvaluateAlertsOnceAsync(stoppingToken); }
                    catch (Exception ex) { logger.LogError(ex, "Proxmox node-alert tick failed"); }

                    var interval = Math.Max(MinimumTickIntervalSeconds, options.CurrentValue.TickIntervalSeconds);
                    nextScheduledSweep = now.AddSeconds(interval);
                }
                else
                {
                    await DrainScanQueueOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Proxmox update tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(WebhookDrainIntervalSeconds), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// V6.11 — drains every connection id queued by the webhook receiver and runs
    /// an immediate scan on each, bypassing the V2.2 schedule (the caller already
    /// knows an upstream event happened) but reusing the same scan service +
    /// notification path so the outcome is identical to a scheduled sweep. Public
    /// for tests. A disabled connection is skipped — the token stays valid but a
    /// paused host shouldn't be scanned.
    /// </summary>
    public async Task DrainScanQueueOnceAsync(CancellationToken cancellationToken)
    {
        var ids = scanQueue.DrainAll();
        if (ids.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<IProxmoxScanService>();

        var connections = await db.ProxmoxConnections.AsTracking()
            .Where(c => ids.Contains(c.Id) && c.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await scanner.CheckConnectionAsync(connection, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proxmox webhook scan failed for connection {ConnectionId}", connection.Id);
            }
        }
    }

    /// <summary>
    /// Runs a single scan pass: loads enabled connections, filters to those due
    /// per their V2.2 schedule, and checks each. Public so tests can drive a
    /// deterministic pass without the timed loop.
    /// </summary>
    public async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<IProxmoxScanService>();

        var enabled = await db.ProxmoxConnections.AsTracking()
            .Where(c => c.Enabled)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var due = enabled
            .Where(c => CheckScheduleEvaluator.IsDue(
                c.ScheduleType, c.CheckEveryHours, c.CheckAtTime, c.CheckOnDayOfWeek,
                c.LastCheckedUtc, now))
            .ToList();

        foreach (var connection in due)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await scanner.CheckConnectionAsync(connection, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proxmox scan failed for connection {ConnectionId}", connection.Id);
            }
        }
    }

    /// <summary>
    /// V6.8.1 — runs one node-health alert pass: evaluates every enabled
    /// connection that has alerting opted in, on every tick (not gated by the
    /// update schedule). Public so tests can drive a deterministic pass.
    /// </summary>
    public async Task EvaluateAlertsOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var alertService = scope.ServiceProvider.GetRequiredService<IProxmoxNodeAlertService>();

        // Only hosts the user both enabled and opted into alerting for — keeps
        // the Proxmox load to nothing until a node is explicitly watched.
        var connections = await db.ProxmoxConnections.AsNoTracking()
            .Where(c => c.Enabled
                && db.ProxmoxNodeAlertSettings.Any(s => s.ProxmoxConnectionId == c.Id && s.Enabled))
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await alertService.EvaluateConnectionAsync(connection, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proxmox node-alert evaluation failed for connection {ConnectionId}", connection.Id);
            }
        }
    }
}
