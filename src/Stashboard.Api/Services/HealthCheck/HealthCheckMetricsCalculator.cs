using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.HealthCheck;

/// <summary>
/// V10.1 — turns the sparse append-only uptime history into the rolled-up metrics shown on the
/// Healthcheck tab: uptime % over 24 h / 7 d / 30 d, the response-time sparkline samples, and the
/// incident log (Down spans with durations). Pure and DB-free so the timeline maths can be
/// unit-tested directly.
/// <para>
/// The timeline is reconstructed by holding each event's status from its timestamp until the next
/// event. <see cref="ServiceStatus.Up"/> and <see cref="ServiceStatus.NeedsAttention"/> count as
/// "up"; <see cref="ServiceStatus.Down"/> as "down"; <see cref="ServiceStatus.Unknown"/>
/// (monitoring off) is excluded from the uptime denominator entirely.
/// </para>
/// </summary>
public static class HealthCheckMetricsCalculator
{
    /// <summary>One reconstructed timeline point: a probe outcome at a moment in time.</summary>
    public readonly record struct Point(DateTime TimestampUtc, ServiceStatus Status, int? ResponseTimeMs, string? Error);

    public const int IncidentWindowDays = 30;
    public const int MaxResponseSamples = 120;

    /// <summary>Cap on incidents returned to the client. A constantly-flapping service can rack up
    /// thousands of Down→Up spans in 30 days; we return the newest <see cref="MaxIncidents"/> and a
    /// total count, so the payload and the rendered list stay bounded. The full raw stream is still
    /// available, paginated, via the events endpoint.</summary>
    public const int MaxIncidents = 100;

    /// <param name="points">All retained events for one (service, target), ascending by timestamp.</param>
    /// <param name="nowUtc">The "now" boundary; the open span runs up to here.</param>
    public static TargetUptimeMetrics Compute(IReadOnlyList<Point> points, DateTime nowUtc)
    {
        var uptime24h = Uptime(points, nowUtc.AddHours(-24), nowUtc);
        var uptime7d = Uptime(points, nowUtc.AddDays(-7), nowUtc);
        var uptime30d = Uptime(points, nowUtc.AddDays(-30), nowUtc);

        var allIncidents = Incidents(points, nowUtc.AddDays(-IncidentWindowDays), nowUtc);
        var samples = ResponseSamples(points, nowUtc.AddDays(-IncidentWindowDays), nowUtc);

        // Incidents are newest-first, so the cap keeps the most recent ones.
        var cappedIncidents = allIncidents.Count > MaxIncidents
            ? allIncidents.GetRange(0, MaxIncidents)
            : allIncidents;

        return new TargetUptimeMetrics(
            uptime24h, uptime7d, uptime30d, points.Count, allIncidents.Count, samples, cappedIncidents);
    }

    /// <summary>
    /// V10.2 — per-day uptime buckets for the public status page's recent-history bar, oldest →
    /// newest. Each bucket is the uptime % over one UTC calendar day; the last bucket runs up to
    /// <paramref name="nowUtc"/>. A bucket with no monitored time is null (rendered "no data").
    /// Pure reuse of <see cref="Uptime"/> so the bar and the headline %s share one timeline.
    /// </summary>
    public static List<DailyUptimeBucket> DailyUptime(IReadOnlyList<Point> points, DateTime nowUtc, int days)
    {
        var buckets = new List<DailyUptimeBucket>(days);
        var firstDay = nowUtc.Date.AddDays(-(days - 1));
        for (var i = 0; i < days; i++)
        {
            var dayStart = firstDay.AddDays(i);
            var dayEnd = dayStart.AddDays(1);
            if (dayEnd > nowUtc) dayEnd = nowUtc;
            buckets.Add(new DailyUptimeBucket(dayStart, Uptime(points, dayStart, dayEnd)));
        }
        return buckets;
    }

    /// <summary>One day of the recent-history bar: the day's start (UTC) and its uptime % (null = no data).</summary>
    public readonly record struct DailyUptimeBucket(DateTime DayStartUtc, double? Uptime);

    /// <summary>Uptime % over [from, now], or null when no monitored (Up/Down) time falls in it.</summary>
    public static double? Uptime(IReadOnlyList<Point> points, DateTime from, DateTime now)
    {
        if (now <= from) return null;

        double upMs = 0, downMs = 0;
        foreach (var seg in BuildSegments(points, from, now))
        {
            var ms = (seg.End - seg.Start).TotalMilliseconds;
            if (ms <= 0) continue;
            if (seg.Status is ServiceStatus.Up or ServiceStatus.NeedsAttention) upMs += ms;
            else if (seg.Status == ServiceStatus.Down) downMs += ms;
            // Unknown contributes to neither — it is "not monitored" time.
        }

        var total = upMs + downMs;
        return total <= 0 ? null : Math.Round(upMs / total * 100, 4);
    }

    private static List<UptimeIncident> Incidents(IReadOnlyList<Point> points, DateTime from, DateTime now)
    {
        var incidents = new List<UptimeIncident>();
        UptimeIncident? openInline = null; // accumulates contiguous Down segments into one incident

        foreach (var seg in BuildSegments(points, from, now))
        {
            if (seg.Status == ServiceStatus.Down)
            {
                openInline = openInline is null
                    ? new UptimeIncident(seg.Start, seg.End, (seg.End - seg.Start).TotalSeconds, false, seg.Error)
                    : openInline with { EndUtc = seg.End, DurationSeconds = (seg.End - openInline.StartUtc).TotalSeconds };
            }
            else if (openInline is not null)
            {
                incidents.Add(openInline);
                openInline = null;
            }
        }

        // A still-open Down span (its end is the "now" boundary) is an ongoing incident.
        if (openInline is not null)
            incidents.Add(openInline with { EndUtc = null, Ongoing = true, DurationSeconds = (now - openInline.StartUtc).TotalSeconds });

        incidents.Reverse(); // newest-first
        return incidents;
    }

    private static List<ResponseSample> ResponseSamples(IReadOnlyList<Point> points, DateTime from, DateTime now)
    {
        var samples = points
            .Where(p => p.TimestampUtc >= from && p.TimestampUtc <= now && p.ResponseTimeMs is not null)
            .OrderBy(p => p.TimestampUtc)
            .Select(p => new ResponseSample(p.TimestampUtc, p.ResponseTimeMs!.Value))
            .ToList();

        // Cap the sparkline to the most recent N points so a long retention window stays light.
        return samples.Count > MaxResponseSamples
            ? samples.GetRange(samples.Count - MaxResponseSamples, MaxResponseSamples)
            : samples;
    }

    /// <summary>
    /// Reconstructs the status segments across [from, now]. The status before <paramref name="from"/>
    /// is carried forward from the last preceding event so a window that opens mid-span is correct.
    /// </summary>
    private static List<Segment> BuildSegments(IReadOnlyList<Point> points, DateTime from, DateTime now)
    {
        var segments = new List<Segment>();

        // Status (and last error) carried into the window from before it started.
        var carried = ServiceStatus.Unknown;
        string? carriedError = null;
        foreach (var p in points)
        {
            if (p.TimestampUtc > from) break;
            carried = p.Status;
            carriedError = p.Error;
        }

        var segStart = from;
        var segStatus = carried;
        var segError = carriedError;

        foreach (var p in points)
        {
            if (p.TimestampUtc <= from || p.TimestampUtc > now) continue;
            if (p.TimestampUtc > segStart)
                segments.Add(new Segment(segStart, p.TimestampUtc, segStatus, segError));
            segStart = p.TimestampUtc;
            segStatus = p.Status;
            segError = p.Error;
        }

        if (now > segStart)
            segments.Add(new Segment(segStart, now, segStatus, segError));

        return segments;
    }

    private readonly record struct Segment(DateTime Start, DateTime End, ServiceStatus Status, string? Error);
}
