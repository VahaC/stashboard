using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Services.HealthCheckSettings;

namespace Stashboard.Api.Services.HealthCheck;

/// <summary>
/// V10.1 — keeps the append-only uptime-history table bounded by deleting rows older than the
/// configured retention window (Settings → Health checks, default 90 days). Runs on a slow loop
/// (every 6 h) — history rows age out over days, not seconds, so frequent scans buy nothing.
/// Mirrors <c>RefreshTokenCleanupHostedService</c>.
/// </summary>
public sealed class HealthCheckHistoryPruneBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<HealthCheckHistoryPruneBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so we don't compete with app startup work.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PruneOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogError(ex, "Uptime-history prune failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Deletes history rows older than the retention window and returns the count.
    /// Public so the retention behaviour can be unit-tested without driving the loop.</summary>
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<IHealthCheckSettingsService>();

        var retentionDays = Math.Max(
            HealthCheckSettingsService.MinimumHistoryRetentionDays,
            (await settings.GetAsync(cancellationToken)).HistoryRetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var deleted = await db.HealthCheckEvents
            .Where(ev => ev.TimestampUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
            logger.LogInformation("Pruned {Count} uptime-history rows older than {RetentionDays} days", deleted, retentionDays);
        return deleted;
    }
}
