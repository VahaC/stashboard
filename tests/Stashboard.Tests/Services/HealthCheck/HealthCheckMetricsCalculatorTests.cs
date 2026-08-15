using Stashboard.Api.Services.HealthCheck;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services.HealthCheck;

using Point = HealthCheckMetricsCalculator.Point;

/// <summary>
/// V10.1 — pure timeline maths for the uptime metrics: uptime % across a known up/down
/// sequence (including the open, still-down span) and the incident log's Down→Up pairing.
/// </summary>
public class HealthCheckMetricsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

    private static Point P(double hoursAgo, ServiceStatus status, int? rt = 100)
        => new(Now.AddHours(-hoursAgo), status, rt, status == ServiceStatus.Down ? "boom" : null);

    [Fact]
    public void Uptime_ComputesPercentageAcrossUpDownSequence()
    {
        // 14h Unknown (excluded) → 4h Up → 2h Down → 4h Up over the last 24h.
        var points = new[] { P(10, ServiceStatus.Up), P(6, ServiceStatus.Down), P(4, ServiceStatus.Up) };

        var uptime = HealthCheckMetricsCalculator.Uptime(points, Now.AddHours(-24), Now);

        // up = 4h + 4h = 8h, down = 2h, total monitored = 10h → 80%.
        Assert.NotNull(uptime);
        Assert.Equal(80.0, uptime!.Value, 3);
    }

    [Fact]
    public void Uptime_CountsOpenStillDownSpan()
    {
        // Up until 2h ago, then Down with no recovery event — the open span is still down.
        var points = new[] { P(10, ServiceStatus.Up), P(2, ServiceStatus.Down) };

        var uptime = HealthCheckMetricsCalculator.Uptime(points, Now.AddHours(-8), Now);

        // Window [-8h, now]: Up [-8h,-2h] = 6h, Down [-2h, now] = 2h → 75%.
        Assert.Equal(75.0, uptime!.Value, 3);
    }

    [Fact]
    public void Uptime_NeedsAttentionCountsAsUp()
    {
        var points = new[] { P(4, ServiceStatus.NeedsAttention) };
        var uptime = HealthCheckMetricsCalculator.Uptime(points, Now.AddHours(-4), Now);
        Assert.Equal(100.0, uptime!.Value, 3);
    }

    [Fact]
    public void Uptime_NullWhenNoMonitoredTime()
    {
        var points = Array.Empty<Point>();
        Assert.Null(HealthCheckMetricsCalculator.Uptime(points, Now.AddHours(-24), Now));
    }

    [Fact]
    public void Incidents_PairsDownToUpWithDuration()
    {
        var points = new[] { P(10, ServiceStatus.Up), P(6, ServiceStatus.Down), P(4, ServiceStatus.Up) };

        var metrics = HealthCheckMetricsCalculator.Compute(points, Now);

        var incident = Assert.Single(metrics.Incidents);
        Assert.False(incident.Ongoing);
        Assert.NotNull(incident.EndUtc);
        Assert.Equal(Now.AddHours(-6), incident.StartUtc);
        Assert.Equal(Now.AddHours(-4), incident.EndUtc);
        Assert.Equal(TimeSpan.FromHours(2).TotalSeconds, incident.DurationSeconds, 1);
    }

    [Fact]
    public void Incidents_OpenSpanIsOngoing()
    {
        var points = new[] { P(10, ServiceStatus.Up), P(3, ServiceStatus.Down) };

        var metrics = HealthCheckMetricsCalculator.Compute(points, Now);

        var incident = Assert.Single(metrics.Incidents);
        Assert.True(incident.Ongoing);
        Assert.Null(incident.EndUtc);
        Assert.Equal(TimeSpan.FromHours(3).TotalSeconds, incident.DurationSeconds, 1);
    }

    [Fact]
    public void Incidents_MergesContiguousDownSamplesIntoOneSpan()
    {
        // Two Down rows in a row (a transition + a later sample) is one incident, not two.
        var points = new[] { P(10, ServiceStatus.Up), P(6, ServiceStatus.Down), P(5, ServiceStatus.Down), P(4, ServiceStatus.Up) };

        var metrics = HealthCheckMetricsCalculator.Compute(points, Now);

        var incident = Assert.Single(metrics.Incidents);
        Assert.Equal(Now.AddHours(-6), incident.StartUtc);
        Assert.Equal(Now.AddHours(-4), incident.EndUtc);
    }

    [Fact]
    public void Incidents_AreCappedButTotalCountReflectsAll()
    {
        // 150 short Down→Up incidents over the last day — well past the cap.
        var points = new List<Point>();
        var start = Now.AddDays(-1);
        for (var i = 0; i < 150; i++)
        {
            points.Add(new Point(start.AddMinutes(i * 8), ServiceStatus.Down, null, "x"));     // down
            points.Add(new Point(start.AddMinutes(i * 8 + 1), ServiceStatus.Up, 100, null));    // recovered
        }

        var metrics = HealthCheckMetricsCalculator.Compute(points, Now);

        Assert.Equal(150, metrics.IncidentCount);                              // true total
        Assert.Equal(HealthCheckMetricsCalculator.MaxIncidents, metrics.Incidents.Count); // capped list
        // Capped list is newest-first.
        Assert.True(metrics.Incidents[0].StartUtc > metrics.Incidents[^1].StartUtc);
    }

    [Fact]
    public void ResponseSamples_AreReturnedAscendingForSparkline()
    {
        var points = new[] { P(3, ServiceStatus.Up, 120), P(2, ServiceStatus.Up, 80), P(1, ServiceStatus.Up, 95) };

        var metrics = HealthCheckMetricsCalculator.Compute(points, Now);

        Assert.Equal(3, metrics.ResponseSamples.Count);
        Assert.Equal(120, metrics.ResponseSamples[0].ResponseTimeMs);
        Assert.Equal(95, metrics.ResponseSamples[^1].ResponseTimeMs);
    }

    // ── V10.2 — public status-page daily history bar ──

    [Fact]
    public void DailyUptime_ReturnsOneBucketPerDayOldestFirst()
    {
        var buckets = HealthCheckMetricsCalculator.DailyUptime(Array.Empty<Point>(), Now, 30);

        Assert.Equal(30, buckets.Count);
        // Oldest first; the last bucket is "today".
        Assert.True(buckets[0].DayStartUtc < buckets[^1].DayStartUtc);
        Assert.Equal(Now.Date, buckets[^1].DayStartUtc);
    }

    [Fact]
    public void DailyUptime_NullForDaysWithNoData_AndPercentForMonitoredDays()
    {
        // Service has been Up since 30 minutes ago only — earlier days have no monitored time.
        var points = new[] { P(0.5, ServiceStatus.Up) };

        var buckets = HealthCheckMetricsCalculator.DailyUptime(points, Now, 30);

        Assert.Null(buckets[0].Uptime);            // a day weeks ago — no data
        Assert.Equal(100.0, buckets[^1].Uptime!.Value, 3); // today — fully up since the only event
    }

    [Fact]
    public void DailyUptime_DownDayIsZeroPercent()
    {
        // Down since the start of yesterday, never recovered.
        var points = new[] { new Point(Now.Date.AddDays(-1), ServiceStatus.Down, null, "boom") };

        var buckets = HealthCheckMetricsCalculator.DailyUptime(points, Now, 3);

        Assert.Equal(0.0, buckets[^2].Uptime!.Value, 3); // yesterday — fully down
    }
}
