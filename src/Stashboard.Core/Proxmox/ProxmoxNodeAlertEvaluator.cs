using Stashboard.Core.Enums;

namespace Stashboard.Core.Proxmox;

/// <summary>V6.8.1 — one metric's classified reading for an evaluation tick.
/// <see cref="Available"/> is <c>false</c> when the source is merely unavailable
/// (SSH/lm-sensors missing, API field absent); the evaluator then leaves the
/// state frozen and never alerts, satisfying "n/a is not crit".</summary>
/// <param name="Category">Which deviation category this reading scores.</param>
/// <param name="Available">Whether the source produced a usable reading.</param>
/// <param name="Level">The classified severity of the reading.</param>
/// <param name="Metric">Human label for the alert (e.g. "CPU", "/dev/sda", "RAM").</param>
/// <param name="Value">The observed value (percent, °C, delta…), for display.</param>
/// <param name="Threshold">The threshold the value was scored against, for display.</param>
public sealed record NodeAlertObservation(
    ProxmoxAlertCategory Category,
    bool Available,
    HealthLevel Level,
    string Metric,
    double? Value,
    double? Threshold);

/// <summary>The transition an evaluation step produced, if any. Each is a
/// one-shot edge — by construction the evaluator emits nothing while an alert
/// sits unchanged, so the notifier never re-fires for a steady deviation.</summary>
public enum NodeAlertTransition
{
    /// <summary>No change worth notifying about.</summary>
    None = 0,

    /// <summary>An inactive category breached for N consecutive evaluations.</summary>
    Fired = 1,

    /// <summary>An active category changed severity (warn→crit or crit→warn),
    /// confirmed across N evaluations.</summary>
    Escalated = 2,

    /// <summary>An active category cleared (Ok for N consecutive evaluations).</summary>
    Recovered = 3,
}

/// <summary>
/// V6.8.1 — the debounce/hysteresis state for one (connection, category). Pure
/// data: the persistence layer mirrors these fields onto a row, but the state
/// machine itself is entity-free so it can be unit-tested without a DB. An alert
/// is "active" while <see cref="ActiveLevel"/> is not <see cref="HealthLevel.Ok"/>.
/// </summary>
public sealed record NodeAlertEvalState(
    HealthLevel ActiveLevel,
    HealthLevel PendingLevel,
    int PendingCount,
    DateTime? FirstSeenUtc,
    string? Metric,
    double? Value,
    double? Threshold)
{
    /// <summary>The starting state — nothing breaching, nothing pending.</summary>
    public static readonly NodeAlertEvalState Inactive =
        new(HealthLevel.Ok, HealthLevel.Ok, 0, null, null, null, null);

    public bool IsActive => ActiveLevel != HealthLevel.Ok;
}

/// <summary>The outcome of a single evaluation step.</summary>
public sealed record NodeAlertEvalResult(NodeAlertEvalState State, NodeAlertTransition Transition);

/// <summary>
/// V6.8.1 — the pure debounce + hysteresis state machine that turns a stream of
/// per-tick classified readings into alert transitions. Mirrors the throttle
/// discipline of the update notifier (a transition is emitted only on an edge),
/// adding the flap-suppression the roadmap calls for:
/// <list type="bullet">
///   <item>A deviation must persist across <c>breachesRequired</c> consecutive
///   evaluations before it <see cref="NodeAlertTransition.Fired"/>.</item>
///   <item>An active alert clears (<see cref="NodeAlertTransition.Recovered"/>)
///   only after the metric returns to Ok — i.e. below the warn band — for the
///   same number of consecutive evaluations (time-window hysteresis).</item>
///   <item>A severity change while active is likewise debounced before it
///   <see cref="NodeAlertTransition.Escalated"/>.</item>
///   <item>An unavailable source freezes the state and never alerts.</item>
/// </list>
/// </summary>
public static class ProxmoxNodeAlertEvaluator
{
    /// <summary>
    /// Advances one category's state by one evaluation. <paramref name="now"/>
    /// stamps <see cref="NodeAlertEvalState.FirstSeenUtc"/> when an alert first
    /// fires. <paramref name="breachesRequired"/> is the debounce count N
    /// (floored at 1).
    /// </summary>
    public static NodeAlertEvalResult Step(
        NodeAlertEvalState prev, NodeAlertObservation obs, int breachesRequired, DateTime now)
    {
        var n = Math.Max(1, breachesRequired);

        // Unavailable source — never alert, never recover, just hold the state.
        if (!obs.Available)
            return new NodeAlertEvalResult(prev, NodeAlertTransition.None);

        var breaching = obs.Level >= HealthLevel.Warn;

        if (!prev.IsActive)
        {
            if (!breaching)
            {
                // Ok and not active: clear any pending breach streak.
                return new NodeAlertEvalResult(
                    prev with { PendingLevel = HealthLevel.Ok, PendingCount = 0, Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold },
                    NodeAlertTransition.None);
            }

            // Counting consecutive breaches at the same candidate level.
            var count = prev.PendingLevel == obs.Level ? prev.PendingCount + 1 : 1;
            if (count >= n)
            {
                var fired = new NodeAlertEvalState(
                    obs.Level, obs.Level, count, now, obs.Metric, obs.Value, obs.Threshold);
                return new NodeAlertEvalResult(fired, NodeAlertTransition.Fired);
            }

            return new NodeAlertEvalResult(
                prev with { PendingLevel = obs.Level, PendingCount = count, Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold },
                NodeAlertTransition.None);
        }

        // ── currently active ──────────────────────────────────────────────────
        if (!breaching)
        {
            // Recovery is debounced too: require N consecutive Ok readings (the
            // metric back below the warn band) before sending "recovered".
            var count = prev.PendingLevel == HealthLevel.Ok ? prev.PendingCount + 1 : 1;
            if (count >= n)
            {
                var cleared = NodeAlertEvalState.Inactive with
                {
                    Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold,
                };
                return new NodeAlertEvalResult(cleared, NodeAlertTransition.Recovered);
            }

            return new NodeAlertEvalResult(
                prev with { PendingLevel = HealthLevel.Ok, PendingCount = count, Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold },
                NodeAlertTransition.None);
        }

        // Still breaching. A severity change (warn↔crit) is debounced before it
        // escalates; FirstSeenUtc is preserved across the change.
        if (obs.Level != prev.ActiveLevel)
        {
            var count = prev.PendingLevel == obs.Level ? prev.PendingCount + 1 : 1;
            if (count >= n)
            {
                var escalated = prev with
                {
                    ActiveLevel = obs.Level, PendingLevel = obs.Level, PendingCount = count,
                    Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold,
                };
                return new NodeAlertEvalResult(escalated, NodeAlertTransition.Escalated);
            }

            return new NodeAlertEvalResult(
                prev with { PendingLevel = obs.Level, PendingCount = count, Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold },
                NodeAlertTransition.None);
        }

        // Steady at the active level — refresh the displayed value, reset the
        // pending streak to the active level so a brief blip doesn't escalate.
        return new NodeAlertEvalResult(
            prev with { PendingLevel = obs.Level, PendingCount = n, Metric = obs.Metric, Value = obs.Value, Threshold = obs.Threshold },
            NodeAlertTransition.None);
    }
}
