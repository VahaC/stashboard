using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.ImagePrune;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// V5.5 — periodic image-prune sweep. Every <see cref="TickIntervalSeconds"/>
/// (default 30 min) the loop wakes up; for each enabled connection where
/// the master switch is on, <c>AllowImagePrune = true</c> and the configured
/// interval has elapsed since the last successful run, it shells out to
/// <see cref="IDockerPruneRunner"/> and persists a
/// <see cref="DockerPruneRunEntity"/> audit row. Connections marked
/// <c>AllowImagePrune = false</c> are skipped silently — they never write
/// an audit row from the scheduled loop.
/// </summary>
/// <remarks>
/// The loop ticks fast enough that even the lowest configured interval
/// (1 hour) is honoured within ~30 min of accuracy without polling the DB
/// every minute. Per-connection runs are sequential to stay friendly to
/// remote Docker hosts.
/// </remarks>
public sealed class DockerImagePruneBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DockerImagePruneBackgroundService> logger)
    : BackgroundService
{
    /// <summary>How often the loop wakes up to evaluate whether any
    /// connection is due. Not the prune interval itself — that lives on
    /// <c>ImagePruneSettingsEntity.IntervalHours</c>.</summary>
    public const int TickIntervalSeconds = 30 * 60;

    private const int StartupDelaySeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Image prune sweep failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(TickIntervalSeconds), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Runs a single sweep pass. Public so tests can drive a deterministic
    /// pass without spinning up the timed loop.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IImagePruneSettingsService>();
        var connectionMapper = scope.ServiceProvider.GetRequiredService<IDockerConnectionMapper>();
        var runner = scope.ServiceProvider.GetRequiredService<IDockerPruneRunner>();

        var settings = await settingsService.GetAsync(cancellationToken);
        if (!settings.Enabled) return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = now - TimeSpan.FromHours(Math.Max(1, settings.IntervalHours));

        // A connection is due when one full interval has elapsed since its
        // last prune — or, for a connection that's never been pruned, since
        // it was created. Using CreatedUtc as the baseline (rather than
        // treating "never pruned" as instantly due) means a freshly added
        // connection isn't pruned within seconds of being created: the first
        // scheduled run lands one interval later. The manual "Prune now"
        // button is always available for an immediate cleanup.
        var due = await db.DockerConnections.AsTracking()
            .Where(c => c.AllowImagePrune
                && (c.LastImagePruneUtc ?? c.CreatedUtc) < threshold)
            .ToListAsync(cancellationToken);
        if (due.Count == 0) return;

        foreach (var connection in due)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var transport = connectionMapper.BuildTransport(connection);
            var request = new DockerPruneRequest(
                ConnectionId: connection.Id,
                Transport: transport,
                IncludeUnused: connection.PruneUnusedImages,
                Trigger: DockerPruneTrigger.Scheduled,
                InitiatedByUserId: null);

            var result = await runner.RunAsync(request, cancellationToken);

            db.DockerPruneRuns.Add(new DockerPruneRunEntity
            {
                Id = Guid.NewGuid(),
                DockerConnectionId = connection.Id,
                InitiatedByUserId = null,
                Trigger = DockerPruneTrigger.Scheduled,
                Status = result.Status,
                IncludedUnused = result.IncludedUnused,
                ImagesDeleted = result.ImagesDeleted,
                SpaceReclaimedBytes = result.SpaceReclaimedBytes,
                StartedUtc = result.StartedUtc,
                CompletedUtc = result.CompletedUtc,
                Error = result.Error,
                CreatedUtc = result.StartedUtc,
            });

            // Only advance LastImagePruneUtc when the daemon successfully
            // answered — a host-unreachable result shouldn't push the next
            // attempt out by the full interval.
            if (result.IsSuccess) connection.LastImagePruneUtc = result.CompletedUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
