using System.Text.RegularExpressions;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Reference parser implementing the OCI Distribution Specification reference
/// grammar with Docker Hub's implicit-registry / <c>library/</c> defaults.
/// Pure function — no I/O, no DI dependencies.
/// </summary>
public sealed class ImageReferenceParser : IImageReferenceParser
{
    // V2.4 — AWS ECR public + private hostnames the auto-detector recognises.
    // Private ECR: <account>.dkr.ecr.<region>.amazonaws.com  (most common)
    // Public ECR:  public.ecr.aws  (no region in the hostname)
    private static readonly Regex EcrPrivateHostRegex = new(
        @"^\d{12}\.dkr\.ecr\.[a-z]{2}-[a-z]+-\d+\.amazonaws\.com$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// V2.4 — when the registry hostname is shaped like a private ECR
    /// endpoint, returns the embedded AWS region (e.g. <c>eu-central-1</c>).
    /// Returns <c>null</c> when the host doesn't match the ECR pattern or
    /// when it's the regionless public-ECR host (<c>public.ecr.aws</c>).
    /// Lives on the parser so the mapper can promote the watch's
    /// <see cref="RegistryAuthType"/> to <see cref="RegistryAuthType.AwsEcr"/>
    /// without the user having to set it manually.
    /// </summary>
    public static string? TryExtractEcrRegion(string registryHost)
    {
        if (string.IsNullOrEmpty(registryHost)) return null;
        var match = EcrPrivateHostRegex.Match(registryHost);
        if (!match.Success) return null;
        var firstDot = registryHost.IndexOf('.');
        if (firstDot < 0) return null;
        // The region sits between ".dkr.ecr." and ".amazonaws.com".
        const string prefix = ".dkr.ecr.";
        const string suffix = ".amazonaws.com";
        var prefixIdx = registryHost.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        var suffixIdx = registryHost.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (prefixIdx < 0 || suffixIdx <= prefixIdx + prefix.Length) return null;
        return registryHost[(prefixIdx + prefix.Length)..suffixIdx];
    }

    /// <summary>V2.4 — returns true when the registry hostname is either
    /// a private ECR endpoint or the public ECR host. The auto-detection
    /// only auto-promotes to AwsEcr for the private flavour because the
    /// public registry doesn't require auth.</summary>
    public static bool LooksLikeEcrHost(string registryHost) =>
        TryExtractEcrRegion(registryHost) is not null;

    private const string DefaultRegistry = "docker.io";
    private const string DefaultTag = "latest";
    private const string DockerHubOfficialNamespace = "library";

    public ImageReference Parse(string reference)
    {
        if (!TryParse(reference, out var result))
            throw new FormatException(
                $"'{reference}' is not a valid Docker image reference. " +
                "Expected '[registry/]repository[:tag][@digest]'.");
        return result;
    }

    public bool TryParse(string reference, out ImageReference result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        var input = reference.Trim();

        // Split off optional @digest (must be 'algo:hex').
        string? digest = null;
        var atIndex = input.IndexOf('@');
        if (atIndex >= 0)
        {
            digest = input[(atIndex + 1)..];
            input = input[..atIndex];
            if (string.IsNullOrEmpty(input)) return false;
            if (!IsDigestShapeValid(digest)) return false;
        }

        // Decide whether the segment before the first '/' is a registry host or a repo namespace.
        // A registry host contains '.', or ':' (port), or is literally 'localhost'.
        string registry;
        string path;
        var firstSlash = input.IndexOf('/');
        if (firstSlash < 0)
        {
            registry = DefaultRegistry;
            path = input;
        }
        else
        {
            var head = input[..firstSlash];
            var tail = input[(firstSlash + 1)..];
            if (LooksLikeRegistryHost(head))
            {
                registry = head;
                path = tail;
            }
            else
            {
                registry = DefaultRegistry;
                path = input;
            }
        }

        if (string.IsNullOrEmpty(path)) return false;

        // Extract the tag — the last ':' in the path, but only when it appears
        // after the last '/' (so a port in a sub-path can't be confused for a tag).
        string? tag = null;
        var lastSlash = path.LastIndexOf('/');
        var lastColon = path.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            tag = path[(lastColon + 1)..];
            path = path[..lastColon];
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(path)) return false;
        }

        // Docker Hub: single-segment refs are official-library images.
        var repository = path;
        if (registry == DefaultRegistry && !repository.Contains('/'))
            repository = $"{DockerHubOfficialNamespace}/{repository}";

        if (!IsRepositoryShapeValid(repository)) return false;
        tag ??= DefaultTag;

        result = new ImageReference(registry, repository, tag, digest);
        return true;
    }

    private static bool LooksLikeRegistryHost(string head) =>
        head.Length > 0 && (head.Contains('.') || head.Contains(':') || string.Equals(head, "localhost", StringComparison.OrdinalIgnoreCase));

    private static bool IsDigestShapeValid(string digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        var sep = digest.IndexOf(':');
        if (sep <= 0 || sep == digest.Length - 1) return false;
        return true;
    }

    private static bool IsRepositoryShapeValid(string repository)
    {
        if (string.IsNullOrEmpty(repository)) return false;
        if (repository.StartsWith('/') || repository.EndsWith('/')) return false;
        if (repository.Contains("//")) return false;
        return true;
    }
}
