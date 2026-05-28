using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Infrastructure.Services;

public sealed class ServiceHealthChecker(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<HealthCheckOptions> options,
    ILogger<ServiceHealthChecker> logger) : IServiceHealthChecker
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

        var opts = options.CurrentValue;
        var maxAttempts = Math.Max(0, opts.RetryCount) + 1;
        var retryDelay = TimeSpan.FromMilliseconds(Math.Max(0, opts.RetryDelayMs));

        // Retry only connection-level failures (DNS, timeout, network, TLS handshake). A real
        // HTTP response — even a 5xx — is never retried: the target answered, so retrying just
        // delays a genuine Down. This kills the false "offline" alerts caused by a single blip.
        ProbeOutcome outcome = default;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            outcome = await CheckCoreAsync(uri, method, expectedStatusRange, allowInvalidCertificates: true, allowHeadFallback: true, cancellationToken);

            if (!outcome.Transient || attempt == maxAttempts || cancellationToken.IsCancellationRequested)
                break;

            logger.LogDebug("Healthcheck transient failure for {Url} (attempt {Attempt}/{Max}): {Error}; retrying", uri, attempt, maxAttempts, outcome.Result.Error);
            try { await Task.Delay(retryDelay, cancellationToken); }
            catch (TaskCanceledException) { break; }
        }

        return outcome.Result;
    }

    private async Task<ProbeOutcome> CheckCoreAsync(
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

            return new ProbeOutcome(
                new HealthCheckResult(
                    ok ? ServiceStatus.Up : ServiceStatus.Down,
                    (int)sw.ElapsedMilliseconds,
                    ok ? null : $"HTTP {code}"),
                Transient: false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeOutcome(new HealthCheckResult(ServiceStatus.Down, (int)sw.ElapsedMilliseconds, "Timeout"), Transient: true);
        }
        catch (Exception ex) when (allowInvalidCertificates && IsCertificateProblem(ex))
        {
            logger.LogDebug(ex, "Healthcheck certificate validation failed for {Url}; retrying insecurely", uri);
            var insecure = await CheckCoreAsync(uri, method, expectedStatusRange, allowInvalidCertificates: false, allowHeadFallback, cancellationToken);
            return insecure.Result.Status == ServiceStatus.Up
                ? insecure with { Result = insecure.Result with { Status = ServiceStatus.NeedsAttention, Error = "Certificate validation was ignored." } }
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
            return new ProbeOutcome(new HealthCheckResult(ServiceStatus.Down, (int)sw.ElapsedMilliseconds, GetErrorMessage(ex)), Transient: true);
        }
    }

    /// <summary>A single probe result plus whether the failure was connection-level
    /// (and therefore worth an in-probe retry). The flag never escapes this class.</summary>
    private readonly record struct ProbeOutcome(HealthCheckResult Result, bool Transient);

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
