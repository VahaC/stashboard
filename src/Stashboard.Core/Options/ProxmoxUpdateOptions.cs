namespace Stashboard.Core.Options;

/// <summary>
/// V6.0 — options for the Proxmox update background scan. Mirrors
/// <see cref="DockerUpdateOptions"/>: the tick cadence is global, while the
/// per-host cadence lives on <c>ProxmoxConnectionEntity</c> via the same V2.2
/// schedule fields (<c>ScheduleType</c> + <c>CheckEveryHours</c> /
/// <c>CheckAtTime</c> / <c>CheckOnDayOfWeek</c>), so the scan only queries a
/// host when it is actually due.
/// </summary>
public class ProxmoxUpdateOptions
{
    public const string SectionName = "ProxmoxUpdate";

    /// <summary>How often the scan wakes up to look for due connections.
    /// Default: 300 (5 minutes). Floor enforced at 30 s by the background
    /// service.</summary>
    public int TickIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// V6.8.1 — node-health alerting evaluates on <em>every</em> tick (not the
    /// per-host update schedule, which can be 24 h apart), so a deviation must
    /// persist across this many consecutive ticks before an alert fires — and
    /// must read Ok for the same number of ticks before "recovered" is sent. The
    /// debounce window in wall-clock time is roughly
    /// <c>AlertConsecutiveBreaches × TickIntervalSeconds</c>. Default 3
    /// (≈15 minutes at the 5-minute tick). Floored at 1 by the evaluator.
    /// </summary>
    public int AlertConsecutiveBreaches { get; set; } = 3;
}
