using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Composes <see cref="IDockerHostClient"/> (Phase 3) and <see cref="IRegistryClient"/>
/// (Phase 2) into a single decision: does the container's current image differ from
/// what its registry advertises? Pure orchestration — no transport, no persistence,
/// no decryption (caller supplies the decrypted <see cref="DockerWatchProfile"/>).
/// </summary>
/// <remarks>
/// Failure semantics: any partial failure (host unreachable, container missing,
/// registry rate-limited, etc.) produces <see cref="DockerUpdateStatus.Error"/>
/// with a human-readable <c>Error</c> string so the dashboard can render an
/// actionable warning. The orchestrator never throws.
/// </remarks>
public sealed class DockerUpdateChecker(
    IDockerHostClient dockerHostClient,
    IRegistryClient registryClient,
    IGitHubReleaseClient gitHubReleaseClient,
    ILogger<DockerUpdateChecker> logger) : IDockerUpdateChecker
{
    private const string GhcrHost = "ghcr.io";

    public async Task<DockerCheckResult> CheckAsync(DockerWatchProfile profile, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var current = await dockerHostClient.GetCurrentImageDigestAsync(
            profile.HostTransport,
            profile.ContainerName, profile.RegistryHost, profile.Repository, cancellationToken);

        if (!current.IsSuccess)
        {
            logger.LogInformation("Docker check failed at host stage: {Status} {Error}", current.Status, current.Error);
            return new DockerCheckResult(
                DockerUpdateStatus.Error, null, null,
                profile.Tag, null,
                FormatHostError(current),
                now);
        }

        // V2.1: when a tag-pattern filter is configured, replace the pinned-tag
        // lookup with "find the best matching tag in the registry and use its
        // digest" — so a pinned `latest` filtered to `^v\d+\.\d+\.\d+$` only
        // ever flips to UpdateAvailable for stable releases.
        // V2.4: hand the registry client a RegistryAuthContext so it can pick
        // the right auth strategy (Auto Bearer / Basic / ECR).
        var auth = BuildRegistryAuth(profile);
        var (latestTag, latestDigestResult) = string.IsNullOrWhiteSpace(profile.TagPatternFilter)
            ? (profile.Tag, await registryClient.GetManifestDigestAsync(
                profile.RegistryHost, profile.Repository, profile.Tag,
                auth, cancellationToken))
            : await ResolveFilteredLatestAsync(profile, auth, now, cancellationToken);

        if (latestDigestResult is null)
        {
            // Filter matched zero tags — surface as UpToDate (no candidate means
            // nothing to upgrade to) but include an Error string so the UI can
            // explain why no comparison happened.
            return new DockerCheckResult(
                DockerUpdateStatus.UpToDate,
                current.Digest, null,
                profile.Tag, null,
                $"No tags matched the filter '{profile.TagPatternFilter}'.",
                now);
        }

        if (!latestDigestResult.IsSuccess)
        {
            logger.LogInformation("Docker check failed at registry stage: {Status} {Error}", latestDigestResult.Status, latestDigestResult.Error);
            return new DockerCheckResult(
                DockerUpdateStatus.Error,
                current.Digest, null,
                profile.Tag, null,
                FormatRegistryError(latestDigestResult),
                now);
        }

        var status = string.Equals(current.Digest, latestDigestResult.Digest, StringComparison.OrdinalIgnoreCase)
            ? DockerUpdateStatus.UpToDate
            : DockerUpdateStatus.UpdateAvailable;

        // V2.3 — enrich GHCR-hosted updates with the matching GitHub release.
        // Best-effort: any failure (no release, rate-limited, network blip)
        // leaves the release fields null without touching the parent status.
        var (releaseUrl, releaseBody) = await TryFetchReleaseAsync(
            profile, status, latestTag, cancellationToken);

        return new DockerCheckResult(
            status,
            current.Digest,
            latestDigestResult.Digest,
            CurrentVersionTag: profile.Tag,
            LatestVersionTag: latestTag,
            Error: null,
            CheckedAtUtc: now,
            LatestReleaseUrl: releaseUrl,
            LatestReleaseBody: releaseBody);
    }

    /// <summary>
    /// V2.3 enrichment hook. Fires only when the registry is GHCR and the
    /// orchestrator has a concrete <c>LatestVersionTag</c> to look up
    /// (no point asking GitHub for "latest" — the registry already told us
    /// which tag won). Status-agnostic: even <c>UpToDate</c> watches get the
    /// release link so the UI can always link out from the modal.
    /// </summary>
    private async Task<(string? Url, string? Body)> TryFetchReleaseAsync(
        DockerWatchProfile profile,
        DockerUpdateStatus status,
        string? latestTag,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(profile.RegistryHost, GhcrHost, StringComparison.OrdinalIgnoreCase))
            return (null, null);
        if (status == DockerUpdateStatus.Error) return (null, null);
        if (string.IsNullOrEmpty(latestTag)) return (null, null);

        // GHCR repository convention: `owner/repo` matches the GitHub repo
        // path one-for-one. Bail out if a more exotic structure is in play
        // (e.g. `org/team/project`) — GitHub Releases lives on a flat
        // owner+repo pair and we'd otherwise 404 every time.
        var slash = profile.Repository.IndexOf('/');
        if (slash <= 0 || slash >= profile.Repository.Length - 1) return (null, null);
        if (profile.Repository.IndexOf('/', slash + 1) >= 0) return (null, null);

        var owner = profile.Repository[..slash];
        var repo = profile.Repository[(slash + 1)..];

        try
        {
            var release = await gitHubReleaseClient.GetReleaseByTagAsync(
                owner, repo, latestTag, profile.GitHubPat, cancellationToken);
            if (release.IsSuccess)
                return (release.HtmlUrl, release.Body);
            logger.LogDebug("GitHub release enrichment skipped for {Owner}/{Repo}@{Tag}: {Status} {Error}",
                owner, repo, latestTag, release.Status, release.Error);
        }
        catch (Exception ex)
        {
            // Defensive: enrichment must never throw out of the orchestrator
            // since CheckAsync's contract is "never throw".
            logger.LogWarning(ex, "GitHub release enrichment threw for {Owner}/{Repo}@{Tag}", owner, repo, latestTag);
        }
        return (null, null);
    }

    /// <summary>
    /// V2.1 tag-pattern resolution. Lists tags, applies the regex, sorts the
    /// matches (semver descending when parseable, lexicographic descending
    /// otherwise) and resolves the digest of the top candidate. Returns
    /// <c>(null, null)</c> when no tag matches the pattern so the caller can
    /// surface a "filter matched nothing" state.
    /// </summary>
    private async Task<(string? Tag, RegistryManifestResult? Digest)> ResolveFilteredLatestAsync(
        DockerWatchProfile profile,
        RegistryAuthContext auth,
        DateTime now,
        CancellationToken cancellationToken)
    {
        Regex regex;
        try { regex = CompileFilter(profile.TagPatternFilter!); }
        catch (RegexParseException ex)
        {
            return (null, new RegistryManifestResult(
                RegistryManifestStatus.InvalidResponse, null, null,
                $"Invalid tag pattern: {ex.Message}", now));
        }

        var tagList = await registryClient.ListTagsAsync(
            profile.RegistryHost, profile.Repository, auth,
            pageSize: 200, cancellationToken: cancellationToken);

        if (!tagList.IsSuccess)
        {
            logger.LogInformation("Tag listing failed: {Status} {Error}", tagList.Status, tagList.Error);
            return (null, new RegistryManifestResult(tagList.Status, null, null,
                tagList.Error ?? $"Tag listing returned {tagList.Status}.", now));
        }

        List<string> matching;
        try
        {
            matching = tagList.Tags.Where(t => regex.IsMatch(t)).ToList();
        }
        catch (RegexMatchTimeoutException ex)
        {
            return (null, new RegistryManifestResult(
                RegistryManifestStatus.InvalidResponse, null, null,
                $"Tag pattern timed out evaluating tags: {ex.Message}", now));
        }

        var best = matching
            .OrderByDescending(t => t, TagVersionComparer.Instance)
            .FirstOrDefault();

        if (best is null) return (null, null);

        var digest = await registryClient.GetManifestDigestAsync(
            profile.RegistryHost, profile.Repository, best,
            auth, cancellationToken);
        return (best, digest);
    }

    /// <summary>V2.4 — converts a profile into the auth context shape the
    /// registry client expects. Centralised so the manifest, tag-listing and
    /// test-connection call sites all agree on the same auth strategy.</summary>
    private static RegistryAuthContext BuildRegistryAuth(DockerWatchProfile profile) =>
        new(profile.RegistryAuthType, profile.RegistryCredentials,
            profile.AwsAccessKeyId, profile.AwsSecretAccessKey, profile.AwsRegion);

    // .NET regex (not ECMAScript) so users can use negative lookahead like
    // `^(?!.*-rc).*$` from the roadmap. Short timeout so a runaway pattern
    // can't stall the background loop.
    private static Regex CompileFilter(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public async Task<DockerConnectionTestResult> TestConnectionAsync(DockerWatchProfile profile, CancellationToken cancellationToken = default)
    {
        var host = await dockerHostClient.TestContainerAsync(
            profile.HostTransport,
            profile.ContainerName, cancellationToken);

        var registry = await registryClient.GetManifestDigestAsync(
            profile.RegistryHost, profile.Repository, profile.Tag,
            BuildRegistryAuth(profile), cancellationToken);

        var registryReachable = registry.Status is RegistryManifestStatus.Ok or RegistryManifestStatus.NotFound;

        string? error = null;
        if (!host.HostReachable)
            error = host.Error;
        else if (!host.ContainerFound)
            error = host.Error;
        else if (!registryReachable)
            error = FormatRegistryError(registry);

        return new DockerConnectionTestResult(
            DockerHostReachable: host.HostReachable,
            ContainerFound: host.ContainerFound,
            RegistryReachable: registryReachable,
            Error: error);
    }

    private static string FormatHostError(DockerHostResult result) =>
        result.Status switch
        {
            DockerHostStatus.HostUnreachable => $"Docker host unreachable: {result.Error}",
            DockerHostStatus.ContainerNotFound => result.Error ?? "Container not found.",
            DockerHostStatus.ImageNotFound => result.Error ?? "Image not found on the host.",
            DockerHostStatus.NoMatchingRepoDigest => result.Error ?? "Container image has no matching RepoDigest.",
            DockerHostStatus.UnsupportedHostType => result.Error ?? "Unsupported Docker host type.",
            _ => result.Error ?? $"Docker host returned {result.Status}.",
        };

    private static string FormatRegistryError(RegistryManifestResult result) =>
        result.Status switch
        {
            RegistryManifestStatus.NotFound => "Tag not found in the registry.",
            RegistryManifestStatus.Unauthorized => "Registry rejected the credentials (or none supplied for a private image).",
            RegistryManifestStatus.RateLimited => "Registry rate-limited the request.",
            RegistryManifestStatus.NetworkError => result.Error ?? "Registry unreachable.",
            RegistryManifestStatus.InvalidResponse => result.Error ?? "Registry returned an unexpected response.",
            _ => result.Error ?? $"Registry returned {result.Status}.",
        };
}
