using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

/// <summary>
/// V5.6 — app-wide health-check tuning, surfaced on the Settings → Health checks
/// page. These are the V5.3.2 offline-alert reliability knobs.
/// </summary>
public sealed record HealthCheckSettingsResponse(
    /// <summary>How often every service is probed, in seconds.</summary>
    int IntervalSeconds,
    /// <summary>Consecutive failed scans before a service is marked Down + an alert fires.</summary>
    int FailureThreshold,
    /// <summary>Extra retries within one probe on a connection-level failure.</summary>
    int RetryCount,
    /// <summary>Delay in milliseconds between in-probe retries.</summary>
    int RetryDelayMs);

/// <summary>V5.6 — update payload for the health-check settings.</summary>
public sealed record UpdateHealthCheckSettingsRequest(
    [Range(10, 86400)] int IntervalSeconds,
    [Range(1, 100)] int FailureThreshold,
    [Range(0, 20)] int RetryCount,
    [Range(0, 60000)] int RetryDelayMs);
