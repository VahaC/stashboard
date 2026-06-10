namespace Stashboard.Core.Enums;

/// <summary>
/// V6.11 — the kind of monitoring change recorded by a
/// <c>ProxmoxMonitoringAuditEntity</c> row. Captures the operator's intent (not
/// just the resulting boolean) so the Audit page reads naturally — "disabled all
/// monitoring", "snoozed until …", "re-enabled". Single-guest toggles, bulk
/// enable/disable, and snooze/unsnooze all funnel through the same audit trail.
/// </summary>
public enum ProxmoxMonitoringChangeType
{
    /// <summary>Update monitoring turned on for the guest.</summary>
    Enabled = 0,

    /// <summary>Update monitoring turned off for the guest.</summary>
    Disabled = 1,

    /// <summary>Guest snoozed for a maintenance window (a future
    /// <c>SnoozedUntil</c> is recorded on the row).</summary>
    Snoozed = 2,

    /// <summary>An active snooze was cleared early (before its window
    /// elapsed).</summary>
    SnoozeCleared = 3,
}
