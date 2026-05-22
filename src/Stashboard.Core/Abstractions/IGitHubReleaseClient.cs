namespace Stashboard.Core.Abstractions;

/// <summary>
/// Outcome categories returned by <see cref="IGitHubReleaseClient.GetReleaseByTagAsync"/>.
/// Mirrors the failure modes of the GitHub REST API plus our own envelope errors.
/// </summary>
public enum GitHubReleaseStatus
{
    /// <summary>Release found and the body fields are populated.</summary>
    Ok = 0,

    /// <summary>The repository exists but no release matches the requested tag.</summary>
    NotFound = 1,

    /// <summary>GitHub returned 401 (bad PAT) or 403 (PAT missing scope).</summary>
    Unauthorized = 2,

    /// <summary>GitHub returned 403 with a rate-limit header set, or 429.</summary>
    RateLimited = 3,

    /// <summary>Network timeout / DNS error / TCP failure.</summary>
    NetworkError = 4,

    /// <summary>Response was not parseable as the expected JSON shape.</summary>
    InvalidResponse = 5,
}

/// <summary>
/// Result returned by <see cref="IGitHubReleaseClient.GetReleaseByTagAsync"/>.
/// On success carries the release's web URL plus a body truncated to
/// <see cref="GitHubReleaseClient.MaxBodyLength"/> chars so we don't store
/// 100 KB changelogs in the database.
/// </summary>
/// <param name="Status">Categorical outcome — always set.</param>
/// <param name="HtmlUrl">Browser URL of the GitHub release (e.g.
/// <c>https://github.com/owner/repo/releases/tag/v1.2.3</c>) on success,
/// otherwise <c>null</c>.</param>
/// <param name="Body">Release notes body (markdown) truncated to the policy
/// length; <c>null</c> on failure or when the release has an empty body.</param>
/// <param name="Error">Human-readable error description on failure.</param>
/// <param name="FetchedAtUtc">When the call was issued.</param>
public sealed record GitHubReleaseResult(
    GitHubReleaseStatus Status,
    string? HtmlUrl,
    string? Body,
    string? Error,
    DateTime FetchedAtUtc)
{
    public bool IsSuccess => Status == GitHubReleaseStatus.Ok && !string.IsNullOrEmpty(HtmlUrl);
}

/// <summary>
/// V2.3 — fetches a GitHub release matching a given image tag so the
/// dashboard can show the upstream changelog inline with the "Update
/// available" badge. Used only when the watch's registry is <c>ghcr.io</c>
/// (the convention is that a GHCR-hosted image lives in the same GitHub
/// repository as its source).
/// </summary>
public interface IGitHubReleaseClient
{
    /// <summary>
    /// Looks up a release by tag via <c>GET /repos/{owner}/{repo}/releases/tags/{tag}</c>.
    /// Public repositories work anonymously; <paramref name="personalAccessToken"/>
    /// is required for private repos and bumps the rate-limit ceiling from
    /// 60 to 5 000 requests / hour for everything else.
    /// </summary>
    /// <param name="owner">GitHub org or user that owns the repository.</param>
    /// <param name="repository">Repository name (no <c>owner/</c> prefix).</param>
    /// <param name="tag">Tag name exactly as published in the registry
    /// (e.g. <c>v1.2.3</c>, <c>2024.04.1</c>).</param>
    /// <param name="personalAccessToken">Optional GitHub PAT. <c>null</c> for
    /// public repositories.</param>
    Task<GitHubReleaseResult> GetReleaseByTagAsync(
        string owner,
        string repository,
        string tag,
        string? personalAccessToken,
        CancellationToken cancellationToken = default);
}
