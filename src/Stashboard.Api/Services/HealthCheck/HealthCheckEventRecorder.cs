using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.HealthCheck;

/// <inheritdoc cref="IHealthCheckEventRecorder"/>
public sealed class HealthCheckEventRecorder : IHealthCheckEventRecorder
{
    public async Task RecordAsync(
        ApplicationDbContext db,
        WebResourceEntity service,
        ServiceStatus previousMainStatus,
        ServiceStatus previousAdditionalStatus,
        int sampleIntervalMinutes,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Last recorded timestamp per target for this service, in one query, so the sample
        // cadence can be applied without a row-per-tick.
        var lastByTarget = await db.HealthCheckEvents
            .Where(ev => ev.WebResourceId == service.Id)
            .GroupBy(ev => ev.Target)
            .Select(g => new { Target = g.Key, Last = g.Max(x => x.TimestampUtc) })
            .ToDictionaryAsync(x => x.Target, x => (DateTime?)x.Last, cancellationToken);

        MaybeRecord(db, service, HealthCheckTarget.Main, previousMainStatus, service.CurrentStatus,
            service.LastResponseTimeMs, service.LastError, lastByTarget, sampleIntervalMinutes, nowUtc);

        // The additional URL only has a meaningful history when one is actually configured.
        if (!string.IsNullOrWhiteSpace(service.AdditionalUrl))
        {
            MaybeRecord(db, service, HealthCheckTarget.Additional, previousAdditionalStatus, service.AdditionalUrlStatus,
                service.AdditionalUrlLastResponseTimeMs, service.AdditionalUrlLastError, lastByTarget, sampleIntervalMinutes, nowUtc);
        }
    }

    private static void MaybeRecord(
        ApplicationDbContext db,
        WebResourceEntity service,
        HealthCheckTarget target,
        ServiceStatus previousStatus,
        ServiceStatus currentStatus,
        int? responseTimeMs,
        string? error,
        IReadOnlyDictionary<HealthCheckTarget, DateTime?> lastByTarget,
        int sampleIntervalMinutes,
        DateTime nowUtc)
    {
        lastByTarget.TryGetValue(target, out var lastEventUtc);
        if (!ShouldRecord(previousStatus, currentStatus, lastEventUtc, nowUtc, sampleIntervalMinutes))
            return;

        db.HealthCheckEvents.Add(new HealthCheckEventEntity
        {
            WebResourceId = service.Id,
            Target = target,
            Status = currentStatus,
            ResponseTimeMs = responseTimeMs,
            Error = error,
            TimestampUtc = nowUtc,
        });
    }

    /// <summary>
    /// Pure decision: write a row when the status changed (a transition — always kept), or, for a
    /// monitored status that has not changed, when the sample cadence has elapsed. An unchanged
    /// <see cref="ServiceStatus.Unknown"/> (monitoring off) is never sampled.
    /// </summary>
    public static bool ShouldRecord(
        ServiceStatus previousStatus,
        ServiceStatus currentStatus,
        DateTime? lastEventUtc,
        DateTime nowUtc,
        int sampleIntervalMinutes)
    {
        if (currentStatus != previousStatus) return true;
        if (currentStatus == ServiceStatus.Unknown) return false;
        if (lastEventUtc is null) return true;
        return nowUtc - lastEventUtc.Value >= TimeSpan.FromMinutes(Math.Max(1, sampleIntervalMinutes));
    }
}
