namespace Stashboard.Core.Abstractions;

/// <summary>
/// Result of parsing a raw Docker / OCI image reference such as
/// <c>nginx</c>, <c>linuxserver/sonarr:latest</c>, <c>ghcr.io/owner/repo:tag</c>,
/// or <c>nginx@sha256:…</c>.
/// </summary>
/// <param name="RegistryHost">Canonical registry host. <c>docker.io</c> when the input
/// omits an explicit registry. Includes the port for self-hosted registries (e.g.
/// <c>localhost:5000</c>).</param>
/// <param name="Repository">Full repository path the registry API expects after
/// <c>/v2/</c>. For Docker Hub single-segment refs this is expanded to
/// <c>library/{name}</c>.</param>
/// <param name="Tag">Tag portion. Defaults to <c>latest</c> when the input has no
/// explicit tag and no digest.</param>
/// <param name="Digest">Optional immutable digest portion (e.g. <c>sha256:abc…</c>)
/// when the reference was pinned with <c>@</c>.</param>
public sealed record ImageReference(
    string RegistryHost,
    string Repository,
    string Tag,
    string? Digest);

/// <summary>
/// Parses raw Docker / OCI image reference strings into their structured components
/// per the OCI Distribution Specification, applying Docker Hub's implicit-registry
/// and <c>library/</c> namespace defaults.
/// </summary>
public interface IImageReferenceParser
{
    /// <summary>Parses <paramref name="reference"/> into an <see cref="ImageReference"/>.</summary>
    /// <exception cref="FormatException">The reference is empty or malformed.</exception>
    ImageReference Parse(string reference);

    /// <summary>Non-throwing variant. Returns <c>true</c> on success.</summary>
    bool TryParse(string reference, out ImageReference result);
}
