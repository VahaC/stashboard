using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// Writes a probe result onto a service entity, applying the consecutive-failure threshold.
/// A service only flips to <see cref="ServiceStatus.Down"/> after <paramref name="failureThreshold"/>
/// consecutive failed probes; until then the prior confirmed status is kept so a single transient
/// blip neither reddens the card nor fires an offline alert. A successful probe resets the counter.
/// Pure and side-effect-free apart from mutating the passed entity — kept separate from the
/// background service so the threshold behaviour can be unit-tested without DB plumbing.
/// </summary>
internal static class HealthCheckStatusEvaluator
{
    public static void Apply(WebResourceEntity service, ServiceCheckResult result, int failureThreshold, DateTime nowUtc)
    {
        var threshold = Math.Max(1, failureThreshold);
        ApplyMain(service, result.Main, threshold, nowUtc);
        ApplyAdditional(service, result.Additional, threshold);
    }

    private static void ApplyMain(WebResourceEntity service, HealthCheckResult main, int threshold, DateTime nowUtc)
    {
        if (main.Status == ServiceStatus.Down)
        {
            service.MainUrlConsecutiveFailures++;
            if (service.MainUrlConsecutiveFailures >= threshold)
            {
                service.CurrentStatus = ServiceStatus.Down;
                service.LastResponseTimeMs = main.ResponseTimeMs;
                service.LastError = main.Error;
            }
            // else: pending confirmation — leave the prior status/error untouched.
        }
        else
        {
            service.MainUrlConsecutiveFailures = 0;
            service.CurrentStatus = main.Status;
            service.LastResponseTimeMs = main.ResponseTimeMs;
            service.LastError = main.Error;
        }

        service.LastCheckedUtc = main.Status == ServiceStatus.Unknown && !service.MainUrlHealthCheckEnabled
            ? null
            : nowUtc;
    }

    private static void ApplyAdditional(WebResourceEntity service, HealthCheckResult? additional, int threshold)
    {
        if (additional is not { } result)
        {
            service.AdditionalUrlStatus = ServiceStatus.Unknown;
            service.AdditionalUrlLastResponseTimeMs = null;
            service.AdditionalUrlLastError = null;
            service.AdditionalUrlConsecutiveFailures = 0;
            return;
        }

        if (result.Status == ServiceStatus.Down)
        {
            service.AdditionalUrlConsecutiveFailures++;
            if (service.AdditionalUrlConsecutiveFailures >= threshold)
            {
                service.AdditionalUrlStatus = ServiceStatus.Down;
                service.AdditionalUrlLastResponseTimeMs = result.ResponseTimeMs;
                service.AdditionalUrlLastError = result.Error;
            }
            // else: pending confirmation — leave the prior status/error untouched.
        }
        else
        {
            service.AdditionalUrlConsecutiveFailures = 0;
            service.AdditionalUrlStatus = result.Status;
            service.AdditionalUrlLastResponseTimeMs = result.ResponseTimeMs;
            service.AdditionalUrlLastError = result.Error;
        }
    }
}
