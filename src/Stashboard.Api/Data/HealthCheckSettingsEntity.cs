using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V5.6 — app-wide tuning for the offline-alert reliability logic introduced in
/// V5.3.2. Single row keyed by <see cref="SingletonId"/>. Seeded from the bound
/// <see cref="Stashboard.Core.Options.HealthCheckOptions"/> on first access, so a
/// deployment that set the values via env vars / appsettings on first run keeps
/// them until an operator changes them on the Settings → Health checks page.
/// Mirrors <see cref="ImagePruneSettingsEntity"/> and <see cref="HostShellSettingsEntity"/>.
/// </summary>
public class HealthCheckSettingsEntity : AuditableEntity
{
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000003");

    /// <summary>How often the background loop probes every monitored service, in
    /// seconds. Floor 10 (the loop never scans more often than every 10 s).
    /// Default 60.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Consecutive failed scans required before a service is marked Down
    /// and an offline notification fires. Floor 1 (notify on first failure).
    /// Default 3.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Extra retries within a single probe when the request fails for a
    /// connection-level reason (DNS, timeout, network, TLS handshake). HTTP status
    /// responses are never retried. Floor 0. Default 2.</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Delay in milliseconds between in-probe retries. Floor 0. Default 1000.</summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>V10.1 — days of uptime history retained before the background prune drops
    /// older rows. Floor 1. Default 90.</summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>V10.1 — minutes between response-time samples written for a service whose
    /// status is unchanged (transitions are always recorded). Floor 1. Default 15.</summary>
    public int HistorySampleIntervalMinutes { get; set; } = 15;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
