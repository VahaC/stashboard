namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.8.0 — resolves a container's <em>official</em> service icon from the
/// <c>homarr-labs/dashboard-icons</c> set. Derives a slug from the image
/// reference (via <see cref="IImageReferenceParser"/>) and fetches the matching
/// <c>.webp</c> from the CDN, returned as a base64 data URI. Best-effort:
/// every method returns <c>null</c> when nothing resolves, and results
/// (including misses) are cached so a refresh doesn't re-hit the CDN.
/// </summary>
public interface IContainerIconResolver
{
    /// <summary>
    /// Derives the dashboard-icons slug for a raw image reference, or <c>null</c>
    /// when the reference can't be parsed. Takes the last path segment of the
    /// parsed repository (so the registry host, namespace, arch prefix and tag
    /// all drop away) and applies the alias table for docker-repo ↔ slug
    /// mismatches. Example: <c>lscr.io/linuxserver/jellyfin:latest</c> → <c>jellyfin</c>.
    /// </summary>
    string? SlugFor(string imageReference);

    /// <summary>
    /// Fetches <c>{base}/webp/{slug}.webp</c> and returns it as a
    /// <c>data:image/webp;base64,…</c> URI, or <c>null</c> when the icon doesn't
    /// exist / the request fails. The result is cached for 24h — negative
    /// results too.
    /// </summary>
    Task<string?> ResolveIconDataUriAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience that combines <see cref="SlugFor"/> + <see cref="ResolveIconDataUriAsync"/>:
    /// resolves the official icon data URI straight from a raw image reference,
    /// or <c>null</c> when no slug derives or no icon resolves.
    /// </summary>
    Task<string?> ResolveIconForImageAsync(string imageReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.8 — maps a Proxmox guest <c>ostype</c> (LXC distro like <c>debian</c> /
    /// <c>ubuntu</c> / <c>alpine</c>, or a VM family like <c>l26</c> / <c>win10</c>)
    /// to a dashboard-icons slug, or <c>null</c> when it can't be mapped.
    /// </summary>
    string? SlugForOs(string? osType);

    /// <summary>
    /// V7.8 — convenience combining <see cref="SlugForOs"/> +
    /// <see cref="ResolveIconDataUriAsync"/>: resolves the official OS icon data
    /// URI straight from a guest <c>ostype</c>, or <c>null</c>.
    /// </summary>
    Task<string?> ResolveIconForOsAsync(string? osType, CancellationToken cancellationToken = default);
}
