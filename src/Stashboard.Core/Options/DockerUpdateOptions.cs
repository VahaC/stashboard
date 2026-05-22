namespace Stashboard.Core.Options;

/// <summary>
/// Options for the Docker update background scan. Tick cadence is global —
/// per-watch cadence lives on <c>DockerWatchEntity</c> via the V2.2 schedule
/// fields (<c>ScheduleType</c> + <c>CheckEveryHours</c> / <c>CheckAtTime</c> /
/// <c>CheckOnDayOfWeek</c>) so the scan only actually queries a registry
/// when a given watch is due.
/// </summary>
public class DockerUpdateOptions
{
    public const string SectionName = "DockerUpdate";

    /// <summary>How often the scan wakes up to look for due watches.
    /// Default: 300 (5 minutes). Floor enforced at 30 s.</summary>
    public int TickIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// V3.2 — number of times the post-start health poll re-inspects the
    /// recreated container before declaring it "did not become healthy".
    /// Default 10 attempts × <see cref="HealthVerificationIntervalSeconds"/>
    /// gives the 30-second window the spec calls for, which comfortably
    /// covers Docker's default healthcheck cadence (10 s interval × 3
    /// retries). 0 disables verification entirely — the recreate is
    /// declared a success the moment <c>StartContainerAsync</c> returns,
    /// matching the V2.7 behaviour.
    /// </summary>
    public int HealthVerificationMaxAttempts { get; set; } = 10;

    /// <summary>
    /// V3.2 — seconds between health-poll attempts. Floor enforced at 1 s
    /// to keep the daemon from being hammered when a misconfiguration
    /// (e.g. tests calling out to a real updater) sets this to 0.
    /// </summary>
    public int HealthVerificationIntervalSeconds { get; set; } = 3;
}
