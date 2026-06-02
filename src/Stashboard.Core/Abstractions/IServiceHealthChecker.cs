using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

public sealed record HealthCheckResult(ServiceStatus Status, int? ResponseTimeMs, string? Error);

public sealed record ServiceCheckResult(HealthCheckResult Main, HealthCheckResult? Additional);

/// <summary>V5.6 — per-probe retry tuning passed in by the caller (sourced from the
/// DB-backed health-check settings). When omitted the checker falls back to its
/// bound <c>HealthCheckOptions</c> config defaults.</summary>
public sealed record HealthCheckRetrySettings(int RetryCount, int RetryDelayMs);

public interface IServiceHealthChecker
{
    Task<ServiceCheckResult> CheckAsync(WebResourceEntity service, HealthCheckRetrySettings? retry = null, CancellationToken cancellationToken = default);
    Task<HealthCheckResult> CheckUrlAsync(string url, HealthCheckMethod method, string? expectedStatusRange, HealthCheckRetrySettings? retry = null, CancellationToken cancellationToken = default);
}
