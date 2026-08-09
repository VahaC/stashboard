using Stashboard.Api.Services.HealthCheck;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services.HealthCheck;

/// <summary>
/// V10.1 — the pure decision behind the append-only history: always record a status transition,
/// sample an unchanged monitored status only past the cadence, and never sample an unchanged
/// "not monitored" (Unknown) tick.
/// </summary>
public class HealthCheckEventRecorderTests
{
    private static readonly DateTime Now = new(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Records_OnStatusTransition_RegardlessOfCadence()
    {
        // Just sampled a moment ago, but the status flipped — a transition is always kept.
        var should = HealthCheckEventRecorder.ShouldRecord(
            ServiceStatus.Up, ServiceStatus.Down, Now.AddSeconds(-5), Now, sampleIntervalMinutes: 15);
        Assert.True(should);
    }

    [Fact]
    public void DoesNotRecord_UnchangedStatus_WithinCadence()
    {
        var should = HealthCheckEventRecorder.ShouldRecord(
            ServiceStatus.Up, ServiceStatus.Up, Now.AddMinutes(-5), Now, sampleIntervalMinutes: 15);
        Assert.False(should);
    }

    [Fact]
    public void Records_UnchangedStatus_PastCadence()
    {
        var should = HealthCheckEventRecorder.ShouldRecord(
            ServiceStatus.Up, ServiceStatus.Up, Now.AddMinutes(-20), Now, sampleIntervalMinutes: 15);
        Assert.True(should);
    }

    [Fact]
    public void Records_UnchangedStatus_WhenNoPriorSample()
    {
        var should = HealthCheckEventRecorder.ShouldRecord(
            ServiceStatus.Up, ServiceStatus.Up, lastEventUtc: null, Now, sampleIntervalMinutes: 15);
        Assert.True(should);
    }

    [Fact]
    public void DoesNotRecord_UnchangedUnknown_EvenWithNoPriorSample()
    {
        var should = HealthCheckEventRecorder.ShouldRecord(
            ServiceStatus.Unknown, ServiceStatus.Unknown, lastEventUtc: null, Now, sampleIntervalMinutes: 15);
        Assert.False(should);
    }
}
