namespace Stashboard.Core.Enums;

/// <summary>
/// V2.2 scheduling mode for Docker watches. Replaces the V1 single
/// <c>CheckIntervalMinutes</c> field with three discrete modes:
/// roll every N hours, fire once per day at a fixed UTC time, or fire
/// once per week on a fixed day + UTC time.
/// </summary>
public enum CheckScheduleType
{
    /// <summary>Repeat every <c>CheckEveryHours</c> hours, rolling from
    /// <c>LastCheckedUtc</c>. Allowed values: 1, 2, 4, 6, 12, 24.</summary>
    Hourly = 0,

    /// <summary>Fire once per day at <c>CheckAtTime</c> (UTC).</summary>
    Daily = 1,

    /// <summary>Fire once per week on <c>CheckOnDayOfWeek</c> at <c>CheckAtTime</c> (UTC).</summary>
    Weekly = 2,
}
