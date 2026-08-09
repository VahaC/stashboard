using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.HealthCheck;

/// <summary>
/// V10.1 — appends uptime-history rows for a just-probed service. Used by both the background
/// scan and the manual "Check now" endpoint so history is captured the same way regardless of
/// what triggered the probe. The recorder only <i>adds</i> to the context; the caller saves.
/// </summary>
public interface IHealthCheckEventRecorder
{
    /// <summary>
    /// Records a history row for each of the service's probed URLs that warrants one (a status
    /// transition, or a sampled tick past the configured cadence). The caller passes the statuses
    /// captured <i>before</i> the probe was applied so transitions can be detected.
    /// </summary>
    Task RecordAsync(
        ApplicationDbContext db,
        WebResourceEntity service,
        ServiceStatus previousMainStatus,
        ServiceStatus previousAdditionalStatus,
        int sampleIntervalMinutes,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
