using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Services;

/// <summary>
/// V7.8.0 — resolves official service icons from the homarr-labs
/// <c>dashboard-icons</c> CDN. Built on the same shape as
/// <see cref="FaviconService"/>: <see cref="IHttpClientFactory"/> +
/// <see cref="IMemoryCache"/>, 24h cache, best-effort, <c>null</c> on failure.
/// </summary>
public sealed class ContainerIconResolver(
    IHttpClientFactory httpFactory,
    IImageReferenceParser parser,
    IMemoryCache cache,
    ILogger<ContainerIconResolver> logger) : IContainerIconResolver
{
    public const string HttpClientName = "container-icon";

    // Single-line base constant — a later phase can swap this for a configurable
    // / self-hosted icon base (deferred, out of scope for V7.8.0).
    private const string IconBaseUrl = "https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    // Small alias table for docker-repo last-segment ↔ dashboard-icons slug
    // mismatches. The common case (jellyfin, sonarr, traefik, nginx, redis, …)
    // matches one-to-one and needs no entry here.
    private static readonly IReadOnlyDictionary<string, string> SlugAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["postgres"] = "postgresql",
            ["pihole"] = "pi-hole",
            ["homeassistant"] = "home-assistant",
        };

    public string? SlugFor(string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return null;
        if (!parser.TryParse(imageReference, out var parsed)) return null;

        // Last path segment of the repository drops the registry host, namespace
        // and any arch prefix (e.g. arm64v8/nginx → nginx); the parser already
        // stripped the tag/digest.
        var repository = parsed.Repository;
        var lastSlash = repository.LastIndexOf('/');
        var segment = (lastSlash >= 0 ? repository[(lastSlash + 1)..] : repository)
            .Trim()
            .ToLowerInvariant();
        if (segment.Length == 0) return null;

        return SlugAliases.TryGetValue(segment, out var alias) ? alias : segment;
    }

    public async Task<string?> ResolveIconDataUriAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        slug = slug.Trim().ToLowerInvariant();

        var cacheKey = $"container-icon:{slug}";
        // A cached miss is stored as a null value: TryGetValue still returns true,
        // so we short-circuit without re-hitting the CDN.
        if (cache.TryGetValue(cacheKey, out string? cached)) return cached;

        var dataUri = await DownloadAsBase64Async($"{IconBaseUrl}/webp/{slug}.webp", cancellationToken);
        cache.Set(cacheKey, dataUri, CacheDuration);
        return dataUri;
    }

    public async Task<string?> ResolveIconForImageAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        var slug = SlugFor(imageReference);
        return slug is null ? null : await ResolveIconDataUriAsync(slug, cancellationToken);
    }

    // V7.8 — Proxmox ostype → dashboard-icons slug. LXC distros map one-to-one
    // (with a couple of aliases); VM families collapse to the generic linux /
    // windows logos. Unknown values fall through to null (placeholder).
    private static readonly IReadOnlyDictionary<string, string> OsSlugs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // LXC distros
            ["debian"] = "debian",
            ["ubuntu"] = "ubuntu",
            ["alpine"] = "alpine-linux",
            ["centos"] = "centos",
            ["fedora"] = "fedora",
            ["archlinux"] = "arch-linux",
            ["arch"] = "arch-linux",
            ["opensuse"] = "opensuse",
            ["gentoo"] = "gentoo",
            ["devuan"] = "devuan",
            ["nixos"] = "nixos",
            ["rocky"] = "rocky-linux",
            ["rockylinux"] = "rocky-linux",
            ["alma"] = "almalinux",
            ["almalinux"] = "almalinux",
            // VM families
            ["l26"] = "linux",
            ["l24"] = "linux",
            ["solaris"] = "linux",
            ["other"] = "linux",
        };

    public string? SlugForOs(string? osType)
    {
        if (string.IsNullOrWhiteSpace(osType)) return null;
        var key = osType.Trim().ToLowerInvariant();
        if (OsSlugs.TryGetValue(key, out var slug)) return slug;
        // Any Windows VM variant (win7 / win10 / win11 / w2k19 / wxp / …).
        if (key.StartsWith("win") || key.StartsWith("w2k")) return "windows";
        return null;
    }

    public async Task<string?> ResolveIconForOsAsync(string? osType, CancellationToken cancellationToken = default)
    {
        var slug = SlugForOs(osType);
        return slug is null ? null : await ResolveIconDataUriAsync(slug, cancellationToken);
    }

    private async Task<string?> DownloadAsBase64Async(string imageUrl, CancellationToken cancellationToken)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.GetAsync(imageUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0) return null;

            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/webp";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to download container icon from {Url}", imageUrl);
            return null;
        }
    }
}
