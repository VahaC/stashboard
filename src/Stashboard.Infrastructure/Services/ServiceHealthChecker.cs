using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Services;

public sealed class ServiceHealthChecker(IHttpClientFactory httpFactory, ILogger<ServiceHealthChecker> logger) : IServiceHealthChecker
{
    public async Task<ServiceCheckResult> CheckAsync(WebResourceEntity service, CancellationToken cancellationToken = default)
    {
        var url = !string.IsNullOrWhiteSpace(service.HealthCheckUrl) ? service.HealthCheckUrl! : service.MainUrl;
        var mainResult = service.MainUrlHealthCheckEnabled
            ? await CheckUrlAsync(url, service.HealthCheckMethod, service.ExpectedStatusRange, cancellationToken)
            : new HealthCheckResult(ServiceStatus.Unknown, null, null);

        HealthCheckResult? additionalResult = null;
        if (!string.IsNullOrWhiteSpace(service.AdditionalUrl))
        {
            additionalResult = service.AdditionalUrlHealthCheckEnabled
                ? await CheckUrlAsync(service.AdditionalUrl, service.HealthCheckMethod, service.ExpectedStatusRange, cancellationToken)
                : new HealthCheckResult(ServiceStatus.Unknown, null, null);
        }

        return new ServiceCheckResult(mainResult, additionalResult);
    }

    public async Task<HealthCheckResult> CheckUrlAsync(string url, HealthCheckMethod method, string? expectedStatusRange, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new HealthCheckResult(ServiceStatus.Down, null, "Invalid URL");

        return await CheckCoreAsync(uri, method, expectedStatusRange, allowInvalidCertificates: true, allowHeadFallback: true, cancellationToken);
    }

    private async Task<HealthCheckResult> CheckCoreAsync(
        Uri uri,
        HealthCheckMethod method,
        string? expectedStatusRange,
        bool allowInvalidCertificates,
        bool allowHeadFallback,
        CancellationToken cancellationToken)
    {
        var clientName = allowInvalidCertificates ? "healthcheck" : "healthcheck-insecure";
        var httpMethod = method == HealthCheckMethod.Head ? HttpMethod.Head : HttpMethod.Get;
        var client = httpFactory.CreateClient(clientName);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(httpMethod, uri);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            var code = (int)resp.StatusCode;

            if (method == HealthCheckMethod.Head
                && allowHeadFallback
                && (resp.StatusCode == HttpStatusCode.MethodNotAllowed || resp.StatusCode == HttpStatusCode.NotImplemented))
            {
                logger.LogDebug("Healthcheck HEAD returned {StatusCode} for {Url}; retrying with GET", resp.StatusCode, uri);
                return await CheckCoreAsync(uri, HealthCheckMethod.Get, expectedStatusRange, allowInvalidCertificates, allowHeadFallback: false, cancellationToken);
            }

            var ok = string.IsNullOrWhiteSpace(expectedStatusRange)
                ? code is >= 200 and < 400
                : MatchesRange(code, expectedStatusRange!);

            return new HealthCheckResult(
                ok ? ServiceStatus.Up : ServiceStatus.Down,
                (int)sw.ElapsedMilliseconds,
                ok ? null : $"HTTP {code}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(ServiceStatus.Down, (int)sw.ElapsedMilliseconds, "Timeout");
        }
        catch (Exception ex) when (allowInvalidCertificates && IsCertificateProblem(ex))
        {
            logger.LogDebug(ex, "Healthcheck certificate validation failed for {Url}; retrying insecurely", uri);
            var insecure = await CheckCoreAsync(uri, method, expectedStatusRange, allowInvalidCertificates: false, allowHeadFallback, cancellationToken);
            return insecure.Status == ServiceStatus.Up
                ? insecure with { Status = ServiceStatus.NeedsAttention, Error = "Certificate validation was ignored." }
                : insecure;
        }
        catch (Exception ex) when (method == HealthCheckMethod.Head && allowHeadFallback)
        {
            logger.LogDebug(ex, "Healthcheck HEAD failed for {Url}; retrying with GET", uri);
            return await CheckCoreAsync(uri, HealthCheckMethod.Get, expectedStatusRange, allowInvalidCertificates, allowHeadFallback: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Healthcheck failed for {Url}", uri);
            return new HealthCheckResult(ServiceStatus.Down, (int)sw.ElapsedMilliseconds, GetErrorMessage(ex));
        }
    }

    private static string GetErrorMessage(Exception exception)
    {
        string? message = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (string.IsNullOrWhiteSpace(current.Message)
                || current.Message.StartsWith("Exception of type", StringComparison.Ordinal))
                continue;

            message = current.Message;
        }

        return message ?? exception.GetType().Name;
    }

    private static bool IsCertificateProblem(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
                return true;

            if (current.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool MatchesRange(int code, string range)
    {
        // Supports formats: "200", "200-299", or comma list "200,204,301-302"
        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, out var single) && single == code) return true;
            }
            else if (int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi))
            {
                if (code >= lo && code <= hi) return true;
            }
        }
        return false;
    }
}
