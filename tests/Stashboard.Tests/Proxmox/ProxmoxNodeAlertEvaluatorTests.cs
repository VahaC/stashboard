using Stashboard.Core.Enums;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Proxmox;

/// <summary>
/// V6.8.1 — debounce / hysteresis tests for the pure alert state machine. No DB,
/// no Proxmox — just the evaluator's edges: fires only after N consecutive
/// breaches, clears only after N consecutive Ok readings, debounces severity
/// changes, and never alerts on an unavailable source.
/// </summary>
public class ProxmoxNodeAlertEvaluatorTests
{
    private const int N = 3;
    private static readonly DateTime Now = new(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

    private static NodeAlertObservation Obs(HealthLevel level, bool available = true, double? value = 96, double? threshold = 95) =>
        new(ProxmoxAlertCategory.Cpu, available, level, "CPU", value, threshold);

    private static NodeAlertEvalResult Run(NodeAlertEvalState state, NodeAlertObservation obs) =>
        ProxmoxNodeAlertEvaluator.Step(state, obs, N, Now);

    // ── firing requires N consecutive breaches ────────────────────────────────

    [Fact]
    public void Fires_OnlyAfterNConsecutiveBreaches()
    {
        var s = NodeAlertEvalState.Inactive;

        var r1 = Run(s, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.None, r1.Transition);
        Assert.False(r1.State.IsActive);

        var r2 = Run(r1.State, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.None, r2.Transition);

        var r3 = Run(r2.State, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.Fired, r3.Transition);
        Assert.True(r3.State.IsActive);
        Assert.Equal(HealthLevel.Crit, r3.State.ActiveLevel);
        Assert.Equal(Now, r3.State.FirstSeenUtc);
    }

    [Fact]
    public void Breach_Streak_ResetsOnInterveningOk()
    {
        var s = NodeAlertEvalState.Inactive;
        s = Run(s, Obs(HealthLevel.Crit)).State;   // 1
        s = Run(s, Obs(HealthLevel.Crit)).State;   // 2
        var ok = Run(s, Obs(HealthLevel.Ok));       // streak broken before N
        Assert.Equal(NodeAlertTransition.None, ok.Transition);
        Assert.Equal(0, ok.State.PendingCount);

        // Next breach starts the count from 1 again, so it takes N more.
        var r1 = Run(ok.State, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.None, r1.Transition);
        var r2 = Run(r1.State, Obs(HealthLevel.Crit));
        var r3 = Run(r2.State, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.Fired, r3.Transition);
    }

    // ── hysteresis: clears only after N consecutive Ok ────────────────────────

    [Fact]
    public void Recovers_OnlyAfterNConsecutiveOk()
    {
        var s = FireCrit();

        var r1 = Run(s, Obs(HealthLevel.Ok));
        Assert.Equal(NodeAlertTransition.None, r1.Transition);
        Assert.True(r1.State.IsActive);   // still active during recovery debounce

        var r2 = Run(r1.State, Obs(HealthLevel.Ok));
        Assert.Equal(NodeAlertTransition.None, r2.Transition);
        Assert.True(r2.State.IsActive);

        var r3 = Run(r2.State, Obs(HealthLevel.Ok));
        Assert.Equal(NodeAlertTransition.Recovered, r3.Transition);
        Assert.False(r3.State.IsActive);
        Assert.Null(r3.State.FirstSeenUtc);
    }

    [Fact]
    public void Recovery_Streak_ResetsOnReBreach()
    {
        var s = FireCrit();
        s = Run(s, Obs(HealthLevel.Ok)).State;   // recovering 1
        s = Run(s, Obs(HealthLevel.Ok)).State;   // recovering 2
        var reBreach = Run(s, Obs(HealthLevel.Crit));   // flips back before clearing
        Assert.Equal(NodeAlertTransition.None, reBreach.Transition);
        Assert.True(reBreach.State.IsActive);

        // A single Ok now is not enough — the recovery streak restarted.
        var ok1 = Run(reBreach.State, Obs(HealthLevel.Ok));
        Assert.Equal(NodeAlertTransition.None, ok1.Transition);
        Assert.True(ok1.State.IsActive);
    }

    // ── severity change debounced ─────────────────────────────────────────────

    [Fact]
    public void Escalates_WarnToCrit_AfterNConsecutive()
    {
        // Build an active warn first.
        var s = NodeAlertEvalState.Inactive;
        for (var i = 0; i < N; i++) s = Run(s, Obs(HealthLevel.Warn, value: 88, threshold: 80)).State;
        Assert.Equal(HealthLevel.Warn, s.ActiveLevel);
        var firstSeen = s.FirstSeenUtc;

        var c1 = Run(s, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.None, c1.Transition);
        var c2 = Run(c1.State, Obs(HealthLevel.Crit));
        var c3 = Run(c2.State, Obs(HealthLevel.Crit));
        Assert.Equal(NodeAlertTransition.Escalated, c3.Transition);
        Assert.Equal(HealthLevel.Crit, c3.State.ActiveLevel);
        // First-seen is preserved across the escalation.
        Assert.Equal(firstSeen, c3.State.FirstSeenUtc);
    }

    [Fact]
    public void SteadyAlert_EmitsNoFurtherTransitions()
    {
        var s = FireCrit();
        for (var i = 0; i < 5; i++)
        {
            var r = Run(s, Obs(HealthLevel.Crit));
            Assert.Equal(NodeAlertTransition.None, r.Transition);
            Assert.True(r.State.IsActive);
            s = r.State;
        }
    }

    // ── unavailable source never alerts ───────────────────────────────────────

    [Fact]
    public void UnavailableSource_NeverFires_AndFreezesState()
    {
        var s = NodeAlertEvalState.Inactive;
        for (var i = 0; i < N * 2; i++)
        {
            var r = Run(s, Obs(HealthLevel.Crit, available: false));
            Assert.Equal(NodeAlertTransition.None, r.Transition);
            Assert.False(r.State.IsActive);
            Assert.Equal(0, r.State.PendingCount);
            s = r.State;
        }
    }

    [Fact]
    public void UnavailableSource_DoesNotRecoverAnActiveAlert()
    {
        var s = FireCrit();
        var r = Run(s, Obs(HealthLevel.Crit, available: false));
        Assert.Equal(NodeAlertTransition.None, r.Transition);
        Assert.True(r.State.IsActive);   // n/a is not "recovered"
    }

    // ── N = 1 fires immediately ───────────────────────────────────────────────

    [Fact]
    public void BreachesRequiredOne_FiresImmediately()
    {
        var r = ProxmoxNodeAlertEvaluator.Step(
            NodeAlertEvalState.Inactive, Obs(HealthLevel.Crit), breachesRequired: 1, Now);
        Assert.Equal(NodeAlertTransition.Fired, r.Transition);
    }

    private static NodeAlertEvalState FireCrit()
    {
        var s = NodeAlertEvalState.Inactive;
        for (var i = 0; i < N; i++) s = ProxmoxNodeAlertEvaluator.Step(s, Obs(HealthLevel.Crit), N, Now).State;
        Assert.True(s.IsActive);
        return s;
    }
}


