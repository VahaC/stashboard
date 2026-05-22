using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.GitHub;

/// <summary>
/// V2.3 — talks to the GitHub REST API to fetch release notes for a given
/// image tag. Used by <see cref="IDockerUpdateChecker"/> to enrich a
/// "Update available" result for GHCR-hosted images.
/// </summary>
/// <remarks>
/// Strategy: a single <c>GET /repos/{owner}/{repo}/releases/tags/{tag}</c>
/// call per check. On 404 we degrade gracefully (the orchestrator just skips
/// enrichment — no error surfaced to the user). The named HTTP client uses
/// a short timeout because this runs inline on the background scan path.
/// </remarks>
public sealed class GitHubReleaseClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubReleaseClient> logger) : IGitHubReleaseClient
{
    public const string HttpClientName = "github-releases";

    /// <summary>Policy length for the persisted release body. Long enough to
    /// fit the typical changelog of a few hundred lines, short enough to keep
    /// `DockerWatches` table rows under a single Postgres page.</summary>
    public const int MaxBodyLength = 2_000;

    private const string ApiBaseUrl = "https://api.github.com";

    public async Task<GitHubReleaseResult> GetReleaseByTagAsync(
        string owner,
        string repository,
        string tag,
        string? personalAccessToken,
        CancellationToken cancellationToken = default)
    {
        var fetchedAt = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(tag))
            return new GitHubReleaseResult(GitHubReleaseStatus.InvalidResponse, null, null,
                "owner, repository and tag are all required.", fetchedAt);

        var url = $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/tags/{Uri.EscapeDataString(tag)}";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(personalAccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new GitHubReleaseResult(GitHubReleaseStatus.NotFound, null, null,
                    $"No GitHub release for tag '{tag}'.", fetchedAt);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new GitHubReleaseResult(GitHubReleaseStatus.Unauthorized, null, null,
                    "GitHub rejected the PAT.", fetchedAt);

            if (response.StatusCode == (HttpStatusCode)429
                || (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response)))
                return new GitHubReleaseResult(GitHubReleaseStatus.RateLimited, null, null,
                    "GitHub API rate-limited the request.", fetchedAt);

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new GitHubReleaseResult(GitHubReleaseStatus.Unauthorized, null, null,
                    "GitHub rejected the request (forbidden).", fetchedAt);

            if (!response.IsSuccessStatusCode)
                return new GitHubReleaseResult(GitHubReleaseStatus.InvalidResponse, null, null,
                    $"GitHub returned HTTP {(int)response.StatusCode}.", fetchedAt);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(payload, fetchedAt);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitHubReleaseResult(GitHubReleaseStatus.NetworkError, null, null,
                "GitHub release request timed out.", fetchedAt);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "GitHub release lookup failed for {Owner}/{Repo}@{Tag}", owner, repository, tag);
            return new GitHubReleaseResult(GitHubReleaseStatus.NetworkError, null, null, ex.Message, fetchedAt);
        }
    }

    private static GitHubReleaseResult Parse(string payload, DateTime fetchedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var htmlUrl = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString()
                : null;

            if (string.IsNullOrEmpty(htmlUrl))
                return new GitHubReleaseResult(GitHubReleaseStatus.InvalidResponse, null, null,
                    "GitHub response missing html_url.", fetchedAt);

            return new GitHubReleaseResult(GitHubReleaseStatus.Ok, htmlUrl,
                Truncate(body, MaxBodyLength), null, fetchedAt);
        }
        catch (JsonException ex)
        {
            return new GitHubReleaseResult(GitHubReleaseStatus.InvalidResponse, null, null,
                $"GitHub response could not be parsed: {ex.Message}", fetchedAt);
        }
    }

    /// <summary>GitHub uses 403 + <c>X-RateLimit-Remaining: 0</c> for rate
    /// limiting on most endpoints — disambiguate from a generic forbidden
    /// (bad PAT, missing scope) so the UI can surface the right message.</summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), out var remaining)
            && remaining == 0)
        {
            return true;
        }
        return response.Headers.TryGetValues("Retry-After", out _);
    }

    private static string? Truncate(string? body, int maxLength)
    {
        if (string.IsNullOrEmpty(body)) return null;
        if (body.Length <= maxLength) return body;
        // Trim mid-word but keep an ellipsis marker so the UI can show
        // "… (truncated)" without re-measuring.
        return string.Concat(body.AsSpan(0, maxLength - 1), "…");
    }
}
