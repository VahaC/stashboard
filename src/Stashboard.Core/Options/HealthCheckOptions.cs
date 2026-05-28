namespace Stashboard.Core.Options;

public class HealthCheckOptions
{
    public const string SectionName = "HealthCheck";

    /// <summary>Interval between scans in seconds. Default: 60.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Per-request HTTP timeout in seconds. Default: 10.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Number of consecutive failed scans required before a service is marked
    /// Down and an offline notification is sent. Guards against false alerts from a single
    /// transient blip (DNS hiccup, momentary network loss). Floor: 1 (notify on first failure).
    /// Default: 3.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Number of extra retries within a single probe when the request fails for a
    /// connection-level reason (DNS, timeout, network unreachable, TLS handshake). HTTP status
    /// responses are never retried. Total attempts = RetryCount + 1. Floor: 0. Default: 2.</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Delay in milliseconds between in-probe retries. Floor: 0. Default: 1000.</summary>
    public int RetryDelayMs { get; set; } = 1000;
}
