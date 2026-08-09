namespace Stashboard.Api.Contracts;

/// <summary>
/// V10.1 — a single Down→Up span derived from the uptime history. <see cref="EndUtc"/> is null
/// and <see cref="Ongoing"/> true while the service is still down (the open span).
/// </summary>
public sealed record UptimeIncident(
    DateTime StartUtc,
    DateTime? EndUtc,
    double DurationSeconds,
    bool Ongoing,
    string? Error);

/// <summary>V10.1 — one response-time data point for the sparkline.</summary>
public sealed record ResponseSample(DateTime TimestampUtc, int ResponseTimeMs);

/// <summary>
/// V10.1 — rolled-up uptime metrics for one of a service's URLs (main or additional).
/// Uptime values are percentages (0–100) or null when there is no monitored time in the window.
/// </summary>
public sealed record TargetUptimeMetrics(
    double? Uptime24h,
    double? Uptime7d,
    double? Uptime30d,
    int EventCount,
    /// <summary>Total incidents in the 30-day window. May exceed <see cref="Incidents"/>.Count,
    /// which is capped (newest-first) so a constantly-flapping service can't return thousands.</summary>
    int IncidentCount,
    IReadOnlyList<ResponseSample> ResponseSamples,
    IReadOnlyList<UptimeIncident> Incidents);

/// <summary>
/// V10.1 — the full uptime payload for a service: per-URL metrics + the retention window
/// the history prunes at. <see cref="Additional"/> is null when the service has no additional URL.
/// </summary>
public sealed record ServiceUptimeResponse(
    int RetentionDays,
    DateTime GeneratedUtc,
    TargetUptimeMetrics Main,
    TargetUptimeMetrics? Additional);

/// <summary>V10.1 — one raw history row for the paginated, newest-first events endpoint.</summary>
public sealed record HealthEventResponse(
    DateTime TimestampUtc,
    string Target,
    string Status,
    int? ResponseTimeMs,
    string? Error);
