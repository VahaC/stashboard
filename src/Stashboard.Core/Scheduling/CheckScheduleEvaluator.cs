using Stashboard.Core.Enums;

namespace Stashboard.Core.Scheduling;

/// <summary>
/// V2.2 due-ness evaluator for Docker watch schedules. Pure functions so the
/// background loop can call them in-memory after loading watches; the same
/// logic powers a "next check at" projection for the UI helper line.
/// </summary>
/// <remarks>
/// All comparisons run in UTC. The helpers treat a missed window (server was
/// down when the daily/weekly target time arrived) as immediately due — the
/// next tick after recovery fires the check.
/// </remarks>
public static class CheckScheduleEvaluator
{
    /// <summary>
    /// Allowed values for <see cref="CheckScheduleType.Hourly"/>. Kept in
    /// sync with the UI dropdown so a tampered payload is rejected at the
    /// mapper level rather than persisting garbage.
    /// </summary>
    public static readonly int[] AllowedHourlyValues = [1, 2, 4, 6, 12, 24];

    /// <summary>
    /// Returns <c>true</c> when the watch has never run, or when its
    /// configured schedule says the next check has come due relative to
    /// <paramref name="nowUtc"/>.
    /// </summary>
    public static bool IsDue(
        CheckScheduleType scheduleType,
        int checkEveryHours,
        TimeOnly? checkAtTime,
        DayOfWeek? checkOnDayOfWeek,
        DateTime? lastCheckedUtc,
        DateTime nowUtc)
    {
        if (lastCheckedUtc is null) return true;
        var last = DateTime.SpecifyKind(lastCheckedUtc.Value, DateTimeKind.Utc);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        return scheduleType switch
        {
            CheckScheduleType.Hourly => now >= last.AddHours(Math.Max(1, checkEveryHours)),
            CheckScheduleType.Daily => checkAtTime is not null
                && IsDailyDue(checkAtTime.Value, last, now),
            CheckScheduleType.Weekly => checkAtTime is not null && checkOnDayOfWeek is not null
                && IsWeeklyDue(checkOnDayOfWeek.Value, checkAtTime.Value, last, now),
            _ => false,
        };
    }

    /// <summary>
    /// Returns the UTC timestamp of the next check, or <c>null</c> when the
    /// schedule is invalid (e.g. Daily without a time-of-day set). Useful for
    /// the UI helper line.
    /// </summary>
    public static DateTime? NextCheckUtc(
        CheckScheduleType scheduleType,
        int checkEveryHours,
        TimeOnly? checkAtTime,
        DayOfWeek? checkOnDayOfWeek,
        DateTime? lastCheckedUtc,
        DateTime nowUtc)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (lastCheckedUtc is null) return now;
        var last = DateTime.SpecifyKind(lastCheckedUtc.Value, DateTimeKind.Utc);

        return scheduleType switch
        {
            CheckScheduleType.Hourly => last.AddHours(Math.Max(1, checkEveryHours)),
            CheckScheduleType.Daily => checkAtTime is null
                ? null
                : NextDaily(checkAtTime.Value, last, now),
            CheckScheduleType.Weekly => checkAtTime is null || checkOnDayOfWeek is null
                ? null
                : NextWeekly(checkOnDayOfWeek.Value, checkAtTime.Value, last, now),
            _ => null,
        };
    }

    // ── Daily ────────────────────────────────────────────────────────────────

    private static bool IsDailyDue(TimeOnly target, DateTime lastCheckedUtc, DateTime nowUtc)
    {
        // Most recent past occurrence of `target` (today if it's already passed,
        // otherwise yesterday). Due iff that occurrence is strictly after
        // lastCheckedUtc and not in the future relative to now.
        var occurrence = MostRecentDailyOccurrence(target, nowUtc);
        return occurrence > lastCheckedUtc;
    }

    private static DateTime MostRecentDailyOccurrence(TimeOnly target, DateTime nowUtc)
    {
        var todayAt = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
            target.Hour, target.Minute, target.Second, DateTimeKind.Utc);
        return todayAt <= nowUtc ? todayAt : todayAt.AddDays(-1);
    }

    private static DateTime NextDaily(TimeOnly target, DateTime lastCheckedUtc, DateTime nowUtc)
    {
        // If we already missed today's window after last check, fire immediately;
        // otherwise the next firing is today's occurrence (or tomorrow's if today's has passed).
        var todayAt = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
            target.Hour, target.Minute, target.Second, DateTimeKind.Utc);
        if (IsDailyDue(target, lastCheckedUtc, nowUtc)) return nowUtc;
        return todayAt > nowUtc ? todayAt : todayAt.AddDays(1);
    }

    // ── Weekly ───────────────────────────────────────────────────────────────

    private static bool IsWeeklyDue(DayOfWeek targetDay, TimeOnly target, DateTime lastCheckedUtc, DateTime nowUtc)
    {
        var occurrence = MostRecentWeeklyOccurrence(targetDay, target, nowUtc);
        return occurrence > lastCheckedUtc;
    }

    private static DateTime MostRecentWeeklyOccurrence(DayOfWeek targetDay, TimeOnly target, DateTime nowUtc)
    {
        // Walk back up to 7 days to find the most recent (day-of-week + time) that's <= now.
        for (var delta = 0; delta < 8; delta++)
        {
            var candidate = nowUtc.AddDays(-delta).Date
                .AddHours(target.Hour).AddMinutes(target.Minute).AddSeconds(target.Second);
            candidate = DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
            if (candidate.DayOfWeek == targetDay && candidate <= nowUtc)
                return candidate;
        }
        // Unreachable for sane inputs (target day always appears within 7 days).
        return nowUtc.AddDays(-7);
    }

    private static DateTime NextWeekly(DayOfWeek targetDay, TimeOnly target, DateTime lastCheckedUtc, DateTime nowUtc)
    {
        if (IsWeeklyDue(targetDay, target, lastCheckedUtc, nowUtc)) return nowUtc;
        // Next firing = the upcoming (day-of-week + time) strictly after now.
        for (var delta = 0; delta < 8; delta++)
        {
            var candidate = nowUtc.AddDays(delta).Date
                .AddHours(target.Hour).AddMinutes(target.Minute).AddSeconds(target.Second);
            candidate = DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
            if (candidate.DayOfWeek == targetDay && candidate > nowUtc)
                return candidate;
        }
        return nowUtc.AddDays(7);
    }
}
