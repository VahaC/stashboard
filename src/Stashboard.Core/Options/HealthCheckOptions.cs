namespace Stashboard.Core.Options;

public class HealthCheckOptions
{
    public const string SectionName = "HealthCheck";

    /// <summary>Interval between scans in seconds. Default: 60.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Per-request HTTP timeout in seconds. Default: 10.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}
