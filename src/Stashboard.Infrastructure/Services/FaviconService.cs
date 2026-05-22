using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Services;

public sealed class FaviconService(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<FaviconService> logger) : IFaviconService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly Regex LinkHrefRegex = new(
        "<link[^>]+href=[\"']([^\"']+)[\"'][^>]*rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*>|<link[^>]+rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*href=[\"']([^\"']+)[\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string?> ResolveFaviconUrlAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)) return null;
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri)) return null;

        var cacheKey = BuildCacheKey(uri);
        if (cache.TryGetValue(cacheKey, out string? cached)) return cached;

        var origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
        var direct = new Uri($"{origin}/favicon.ico");
        var resolved = await TryResolveAsync(uri, direct, cancellationToken)
            ?? $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(uri.Host)}&sz=64";

        cache.Set(cacheKey, resolved, CacheDuration);
        return resolved;
    }

    public void InvalidateSiteFaviconCache(string siteUrl)
    {
        if (string.IsNullOrWhiteSpace(siteUrl))
            return;

        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var siteUri))
            return;

        cache.Remove(BuildCacheKey(siteUri));
    }

    private static string BuildCacheKey(Uri uri)
        => $"favicon:{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";

    private async Task<string?> TryResolveAsync(Uri pageUri, Uri directFaviconUri, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(directFaviconUri, allowInvalidCertificates: true, cancellationToken))
            return directFaviconUri.ToString();

        var discovered = await TryReadHtmlIconAsync(pageUri, allowInvalidCertificates: true, cancellationToken);
        return discovered?.ToString();
    }

    private async Task<bool> ExistsAsync(Uri uri, bool allowInvalidCertificates, CancellationToken cancellationToken)
    {
        var client = httpFactory.CreateClient(allowInvalidCertificates ? "favicon" : "favicon-insecure");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode && IsImageContent(response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex) when (allowInvalidCertificates && IsCertificateProblem(ex))
        {
            logger.LogDebug(ex, "Favicon certificate validation failed for {Url}; retrying insecurely", uri);
            return await ExistsAsync(uri, allowInvalidCertificates: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Direct favicon probe failed for {Url}", uri);
            return false;
        }
    }

    private async Task<Uri?> TryReadHtmlIconAsync(Uri pageUri, bool allowInvalidCertificates, CancellationToken cancellationToken)
    {
        var client = httpFactory.CreateClient(allowInvalidCertificates ? "favicon" : "favicon-insecure");
        try
        {
            using var response = await client.GetAsync(pageUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = LinkHrefRegex.Matches(html)
                .Select(static match => string.IsNullOrWhiteSpace(match.Groups[1].Value) ? match.Groups[2].Value : match.Groups[1].Value)
                .FirstOrDefault(IsCandidateIconHref);

            if (string.IsNullOrWhiteSpace(match))
                return null;

            return Uri.TryCreate(pageUri, WebUtility.HtmlDecode(match), out var resolvedUri)
                ? resolvedUri
                : null;
        }
        catch (Exception ex) when (allowInvalidCertificates && IsCertificateProblem(ex))
        {
            logger.LogDebug(ex, "Page icon discovery certificate validation failed for {Url}; retrying insecurely", pageUri);
            return await TryReadHtmlIconAsync(pageUri, allowInvalidCertificates: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HTML favicon discovery failed for {Url}", pageUri);
            return null;
        }
    }

    private static bool IsImageContent(string? mediaType)
        => mediaType is not null
            && (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));

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

    private static bool IsCandidateIconHref(string href)
        => href.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || href.Contains("icon", StringComparison.OrdinalIgnoreCase);
}
