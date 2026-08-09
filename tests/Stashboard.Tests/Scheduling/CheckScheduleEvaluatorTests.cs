using Stashboard.Core.Enums;
using Stashboard.Core.Scheduling;

namespace Stashboard.Tests.Scheduling;

/// <summary>
/// Unit tests for the pure V2.2 schedule due-ness logic. Keeps the
/// background-service tests focused on the loop / persistence and leaves the
/// edge-case time-math here.
/// </summary>
public class CheckScheduleEvaluatorTests
{
    private static readonly DateTime Anchor = new(2026, 5, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── Hourly ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsDue_HourlyNeverChecked_IsAlwaysDue()
    {
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Hourly, 24, null, null,
            lastCheckedUtc: null, nowUtc: Anchor);
        Assert.True(due);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    public void IsDue_HourlyJustChecked_IsNotDue(int every)
    {
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Hourly, every, null, null,
            lastCheckedUtc: Anchor.AddMinutes(-5), nowUtc: Anchor);
        Assert.False(due);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    public void IsDue_HourlyIntervalElapsed_IsDue(int every)
    {
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Hourly, every, null, null,
            lastCheckedUtc: Anchor.AddHours(-every).AddSeconds(-1), nowUtc: Anchor);
        Assert.True(due);
    }

    // ── Daily ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsDue_DailyTargetTimeAlreadyPassedToday_LastCheckYesterday_IsDue()
    {
        // Target = 08:00 UTC; now = 10:00. Last check = yesterday at 09:00
        // (after the previous day's target). Today's target (08:00) is more
        // recent than the last check → due.
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Daily, 24, new TimeOnly(8, 0), null,
            lastCheckedUtc: Anchor.AddDays(-1).AddHours(-1), nowUtc: Anchor);
        Assert.True(due);
    }

    [Fact]
    public void IsDue_DailyTargetTimeNotYet_LastCheckEarlierToday_IsNotDue()
    {
        // Target = 23:00 UTC; now = 10:00. Last check = 06:00 today. The most
        // recent occurrence of the target is yesterday at 23:00 — older than
        // last check → not due.
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Daily, 24, new TimeOnly(23, 0), null,
            lastCheckedUtc: Anchor.AddHours(-4), nowUtc: Anchor);
        Assert.False(due);
    }

    [Fact]
    public void IsDue_DailyMissedWindow_ServerWasDown_IsDue()
    {
        // Target = 08:00 UTC daily; now = 10:00 today; last check = 5 days ago.
        // Today's target at 08:00 is more recent than last check → due.
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Daily, 24, new TimeOnly(8, 0), null,
            lastCheckedUtc: Anchor.AddDays(-5), nowUtc: Anchor);
        Assert.True(due);
    }

    // ── Weekly ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsDue_WeeklyJustHitTargetDayAndTime_LastCheckLastWeek_IsDue()
    {
        // Anchor is Friday 2026-05-15 10:00 UTC. Target = Friday 09:00.
        var lastWeek = Anchor.AddDays(-7).AddHours(-1);
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Weekly, 24, new TimeOnly(9, 0), DayOfWeek.Friday,
            lastCheckedUtc: lastWeek, nowUtc: Anchor);
        Assert.True(due);
    }

    [Fact]
    public void IsDue_WeeklyJustChecked_NotDueAgainForAWeek()
    {
        // Just checked 5 minutes ago — never due regardless of target.
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Weekly, 24, new TimeOnly(9, 0), DayOfWeek.Friday,
            lastCheckedUtc: Anchor.AddMinutes(-5), nowUtc: Anchor);
        Assert.False(due);
    }

    [Fact]
    public void IsDue_WeeklyTargetDayInFuture_IsNotDue()
    {
        // Anchor = Friday; target = Saturday 09:00. Most recent Saturday
        // occurrence is 6 days ago. Last check = 3 days ago → not due.
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Weekly, 24, new TimeOnly(9, 0), DayOfWeek.Saturday,
            lastCheckedUtc: Anchor.AddDays(-3), nowUtc: Anchor);
        Assert.False(due);
    }

    [Fact]
    public void IsDue_WeeklyMissedTwoWindows_StillDueImmediately()
    {
        var due = CheckScheduleEvaluator.IsDue(
            CheckScheduleType.Weekly, 24, new TimeOnly(9, 0), DayOfWeek.Friday,
            lastCheckedUtc: Anchor.AddDays(-20), nowUtc: Anchor);
        Assert.True(due);
    }

    // ── NextCheckUtc ────────────────────────────────────────────────────────

    [Fact]
    public void NextCheckUtc_Hourly_AddsHoursToLastCheck()
    {
        var last = Anchor.AddHours(-1);
        var next = CheckScheduleEvaluator.NextCheckUtc(
            CheckScheduleType.Hourly, 4, null, null,
            lastCheckedUtc: last, nowUtc: Anchor);
        Assert.Equal(last.AddHours(4), next);
    }

    [Fact]
    public void NextCheckUtc_DailyAlreadyPassedAndDue_ReturnsNow()
    {
        // Last check more than a day ago, target = 08:00 → due now → next
        // check is "now".
        var next = CheckScheduleEvaluator.NextCheckUtc(
            CheckScheduleType.Daily, 24, new TimeOnly(8, 0), null,
            lastCheckedUtc: Anchor.AddDays(-2), nowUtc: Anchor);
        Assert.Equal(Anchor, next);
    }

    [Fact]
    public void NextCheckUtc_DailyTargetLaterToday_ReturnsTodayAtTarget()
    {
        // Target = 23:00 today; last check = 06:00 today → next check today at 23:00.
        var next = CheckScheduleEvaluator.NextCheckUtc(
            CheckScheduleType.Daily, 24, new TimeOnly(23, 0), null,
            lastCheckedUtc: Anchor.AddHours(-4), nowUtc: Anchor);
        var expected = new DateTime(Anchor.Year, Anchor.Month, Anchor.Day, 23, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, next);
    }
}


