using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

public sealed record HealthCheckResult(ServiceStatus Status, int? ResponseTimeMs, string? Error);

public sealed record ServiceCheckResult(HealthCheckResult Main, HealthCheckResult? Additional);

public interface IServiceHealthChecker
{
    Task<ServiceCheckResult> CheckAsync(WebResourceEntity service, CancellationToken cancellationToken = default);
    Task<HealthCheckResult> CheckUrlAsync(string url, HealthCheckMethod method, string? expectedStatusRange, CancellationToken cancellationToken = default);
}
