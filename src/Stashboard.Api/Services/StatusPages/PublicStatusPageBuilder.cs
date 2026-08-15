using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Services.HealthCheck;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.StatusPages;

/// <summary>
/// V10.2 — builds the public status-page payload from a published page. Reuses the V10.1
/// <see cref="HealthCheckMetricsCalculator"/> for the uptime maths so the public bar and the
/// owner's Uptime tab share one timeline. Only the main URL drives the public status / uptime —
/// the additional URL is an owner-side health-check detail, not a public service.
/// </summary>
public interface IPublicStatusPageBuilder
{
    Task<PublicStatusPageResponse> BuildAsync(StatusPageEntity page, CancellationToken cancellationToken);
}

public sealed class PublicStatusPageBuilder(ApplicationDbContext db) : IPublicStatusPageBuilder
{
    /// <summary>Days shown in the public recent-history bar (matches the calculator's window).</summary>
    private const int HistoryDays = HealthCheckMetricsCalculator.IncidentWindowDays;

    public async Task<PublicStatusPageResponse> BuildAsync(StatusPageEntity page, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-HistoryDays);

        var ordered = page.Items
            .Where(i => i.WebResource is not null)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.WebResource!.Name)
            .ToList();

        var services = new List<PublicStatusServiceResponse>(ordered.Count);
        foreach (var item in ordered)
        {
            var service = item.WebResource!;
            var points = await BuildMainPointsAsync(service.Id, windowStart, cancellationToken);

            var buckets = HealthCheckMetricsCalculator.DailyUptime(points, now, HistoryDays)
                .Select(b => new PublicStatusHistoryBucket(b.DayStartUtc, b.Uptime, BucketStatus(b.Uptime)))
                .ToList();

            services.Add(new PublicStatusServiceResponse(
                Name: string.IsNullOrWhiteSpace(item.DisplayName) ? service.Name : item.DisplayName!,
                Status: service.CurrentStatus.ToString(),
                Uptime24h: HealthCheckMetricsCalculator.Uptime(points, now.AddHours(-24), now),
                Uptime7d: HealthCheckMetricsCalculator.Uptime(points, now.AddDays(-7), now),
                Uptime30d: HealthCheckMetricsCalculator.Uptime(points, now.AddDays(-30), now),
                History: buckets));
        }

        return new PublicStatusPageResponse(
            Title: page.Title,
            Description: page.Description,
            GeneratedUtc: now,
            OverallStatus: OverallStatus(ordered.Select(i => i.WebResource!.CurrentStatus)),
            Services: services);
    }

    /// <summary>Ascending main-URL timeline for one service: the single event before the window
    /// (carried status) plus every in-window main-URL event.</summary>
    private async Task<List<HealthCheckMetricsCalculator.Point>> BuildMainPointsAsync(
        Guid serviceId, DateTime windowStart, CancellationToken cancellationToken)
    {
        var points = new List<HealthCheckMetricsCalculator.Point>();

        var preceding = await db.HealthCheckEvents.AsNoTracking()
            .Where(ev => ev.WebResourceId == serviceId && ev.Target == HealthCheckTarget.Main && ev.TimestampUtc < windowStart)
            .OrderByDescending(ev => ev.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (preceding is not null)
            points.Add(ToPoint(preceding));

        var windowEvents = await db.HealthCheckEvents.AsNoTracking()
            .Where(ev => ev.WebResourceId == serviceId && ev.Target == HealthCheckTarget.Main && ev.TimestampUtc >= windowStart)
            .OrderBy(ev => ev.TimestampUtc)
            .ToListAsync(cancellationToken);
        points.AddRange(windowEvents.Select(ToPoint));
        return points;
    }

    private static HealthCheckMetricsCalculator.Point ToPoint(HealthCheckEventEntity ev) =>
        new(ev.TimestampUtc, ev.Status, ev.ResponseTimeMs, ev.Error);

    /// <summary>One day's bar colour bucketed from its uptime %.</summary>
    private static string BucketStatus(double? uptime) => uptime switch
    {
        null => "unknown",
        >= 100 => "up",
        <= 0 => "down",
        _ => "partial",
    };

    /// <summary>Aggregate banner state from the selected services' live status.</summary>
    private static string OverallStatus(IEnumerable<ServiceStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Any(s => s == ServiceStatus.Down)) return "down";
        if (list.Any(s => s == ServiceStatus.NeedsAttention)) return "degraded";
        if (list.Any(s => s == ServiceStatus.Up)) return "operational";
        return "unknown";
    }
}
