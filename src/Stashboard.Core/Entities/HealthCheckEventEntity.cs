using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V10.1 — an append-only uptime-history row. One per recorded probe outcome for a service's
/// <see cref="Target"/> URL, written by the health-check scan (and a manual "Check now") only
/// on a <b>status transition</b> or on a sampled cadence — never one row per tick per URL — so
/// the table stays bounded and a background prune drops rows past the retention window.
/// <para>
/// The status timeline is reconstructed from these sparse rows: each event's <see cref="Status"/>
/// holds from its <see cref="TimestampUtc"/> until the next event. Uptime %, the response-time
/// sparkline and the incident log are all derived from this stream by
/// <c>HealthCheckMetricsCalculator</c>; nothing here is exported in a backup (it is
/// runtime-derived telemetry, re-built on the live instance).
/// </para>
/// <para>
/// Uses a lean autoincrement <see cref="long"/> key rather than a Guid — this is the one
/// high-churn table in the schema, so the smaller, monotonic key keeps inserts and the
/// <c>(WebResourceId, Target, TimestampUtc)</c> index cheap.
/// </para>
/// </summary>
public class HealthCheckEventEntity
{
    public long Id { get; set; }

    /// <summary>Owning service. Cascade-deleted with it — history is meaningless without it.</summary>
    public Guid WebResourceId { get; set; }
    public WebResourceEntity? WebResource { get; set; }

    /// <summary>Which of the service's URLs this row tracks (main vs additional).</summary>
    public HealthCheckTarget Target { get; set; }

    /// <summary>The probe outcome at <see cref="TimestampUtc"/>. <see cref="ServiceStatus.Up"/>
    /// and <see cref="ServiceStatus.NeedsAttention"/> count as "up" for uptime; <see cref="ServiceStatus.Down"/>
    /// counts as "down"; <see cref="ServiceStatus.Unknown"/> (monitoring off) is excluded from
    /// the uptime denominator.</summary>
    public ServiceStatus Status { get; set; }

    /// <summary>Probe latency in milliseconds, when one was measured. Null for a status with no
    /// timing (e.g. a transition to Unknown). Feeds the response-time sparkline.</summary>
    public int? ResponseTimeMs { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    public DateTime TimestampUtc { get; set; }
}
