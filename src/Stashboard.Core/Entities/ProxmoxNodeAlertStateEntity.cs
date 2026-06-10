using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.8.1 — the persisted debounce/hysteresis state for one (connection,
/// category). The durable mirror of the pure
/// <c>ProxmoxNodeAlertEvaluator</c>'s state: each evaluation tick loads the row,
/// advances it through the state machine, and writes it back. An alert is
/// "active" (shown on the Alerts tab, counted in the notification signature)
/// while <see cref="ActiveLevel"/> is not <see cref="HealthLevel.Ok"/>.
/// Keyed by (connection, category) the way <c>ProxmoxGuestEntity</c> keys on
/// (connection, vmid). Cascades away with the host.
/// </summary>
public class ProxmoxNodeAlertStateEntity : BaseEntity
{
    public Guid ProxmoxConnectionId { get; set; }
    public ProxmoxConnectionEntity ProxmoxConnection { get; set; } = default!;

    /// <summary>A single category bit (<c>Cpu</c>, <c>Memory</c>, …) — never a
    /// combined mask.</summary>
    public ProxmoxAlertCategory Category { get; set; }

    /// <summary>The confirmed, currently-firing severity. <see cref="HealthLevel.Ok"/>
    /// means not active.</summary>
    public HealthLevel ActiveLevel { get; set; } = HealthLevel.Ok;

    /// <summary>The candidate level being counted toward the debounce threshold.</summary>
    public HealthLevel PendingLevel { get; set; } = HealthLevel.Ok;

    /// <summary>How many consecutive evaluations the candidate has held.</summary>
    public int PendingCount { get; set; }

    /// <summary>When the currently-active alert first fired (carried into the
    /// notification + the Alerts tab). <c>null</c> when not active.</summary>
    public DateTime? FirstSeenUtc { get; set; }

    /// <summary>Human label for the breaching metric (e.g. "CPU", "RAM",
    /// "/dev/sda", "Core 0").</summary>
    public string? Metric { get; set; }

    /// <summary>Last observed value (percent, °C, error-delta…), for display.</summary>
    public double? Value { get; set; }

    /// <summary>Threshold the value was scored against, for display.</summary>
    public double? Threshold { get; set; }

    /// <summary>V6.8.1 — Network category only: the previous total NIC
    /// error+drop counter, so the next tick can compute the spike delta. Not
    /// used by the other categories.</summary>
    public long? NicCounterBaseline { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
