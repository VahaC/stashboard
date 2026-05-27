using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Unit tests for the <see cref="DockerUpdateChecker"/> orchestrator. The host
/// client and registry client are mocked — those have their own dedicated test
/// suites — so these tests only exercise the comparison/error-routing logic
/// that lives in the orchestrator itself.
/// </summary>
public class DockerUpdateCheckerTests
{
    private const string DigestA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    // ── CheckAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_BothDigestsMatch_ReturnsUpToDate()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA, "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
        Assert.Equal(DigestA, result.CurrentDigest);
        Assert.Equal(DigestA, result.LatestDigest);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_DigestsDiffer_ReturnsUpdateAvailable()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA, "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(DigestA, result.CurrentDigest);
        Assert.Equal(DigestB, result.LatestDigest);
    }

    [Fact]
    public async Task CheckAsync_DigestComparisonIsCaseInsensitive()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, "SHA256:abc", "library/nginx@SHA256:abc", "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.Ok, "sha256:ABC", null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckAsync_HostUnreachable_ReturnsErrorAndSkipsRegistry()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.HostUnreachable, null, null, null, "dns fail"));
        var registry = new Mock<IRegistryClient>();
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Contains("dns fail", result.Error);
        registry.Verify(r => r.GetManifestDigestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_ContainerNotFound_ReturnsError()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.ContainerNotFound, null, null, null, "missing container"));
        var registry = new Mock<IRegistryClient>();
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Contains("missing container", result.Error);
    }

    [Fact]
    public async Task CheckAsync_RegistryRateLimited_ReturnsErrorButKeepsCurrentDigest()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA, "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.RateLimited, null, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Equal(DigestA, result.CurrentDigest);
        Assert.Null(result.LatestDigest);
        Assert.Contains("rate-limited", result.Error);
    }

    [Fact]
    public async Task CheckAsync_RegistryNotFound_ReturnsErrorWithMeaningfulMessage()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA, "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.NotFound, null, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_RegistryUnauthorized_ReturnsErrorMentioningCredentials()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA, "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.Unauthorized, null, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Contains("credentials", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── TagPatternFilter (V2.1) ──────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_FilterSet_PicksHighestMatchingTagAndComparesItsDigest()
    {
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.Ok,
                new[] { "v1.0.0", "v1.1.0", "v2.0.0", "v2.0.1-rc1" }, null, DateTime.UtcNow));
        // Expect the orchestrator to ask for the digest of the highest stable tag.
        registry.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), "v2.0.0",
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"^v\d+\.\d+\.\d+$"));

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.0.0", result.LatestVersionTag);
        Assert.Equal(DigestB, result.LatestDigest);
    }

    [Fact]
    public async Task CheckAsync_FilterSet_PreReleaseOnly_StaysUpToDate_DoD()
    {
        // DoD from V2.1: a watch with `^v\d+\.\d+\.\d+$` does NOT flip to
        // UpdateAvailable when the registry's newest tag is `v2.1.0-rc1`.
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.Ok,
                new[] { "v2.0.0", "v2.1.0-rc1" }, null, DateTime.UtcNow));
        // The matching stable tag's digest matches the running container → UpToDate.
        registry.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), "v2.0.0",
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"^v\d+\.\d+\.\d+$"));

        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
        Assert.Equal("v2.0.0", result.LatestVersionTag);
        Assert.Equal(DigestA, result.LatestDigest);
    }

    [Fact]
    public async Task CheckAsync_StableOnlyPreset_PrefersSemverOverNonSemverTags()
    {
        // Regression: the "Stable only" UI preset `^(?!.*-(rc|beta|alpha)).*$`
        // also admits non-semver tags like `nightly`/`latest`. The orchestrator
        // must still pick the highest real version (1.28.0), not `nightly`
        // (which sorts above digits ordinally) — otherwise the watch would be
        // stuck reporting UpdateAvailable against a moving `nightly` digest.
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.Ok,
                new[] { "latest", "nightly", "1.27.0", "1.28.0" }, null, DateTime.UtcNow));
        registry.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), "1.28.0",
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"^(?!.*-(rc|beta|alpha)).*$"));

        Assert.Equal("1.28.0", result.LatestVersionTag);
        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckAsync_FilterSet_UnanchoredPattern_StillRequiresFullMatch()
    {
        // `v\d+\.\d+\.\d+` (no ^…$) must NOT accept `v2.0.0-rc1` via a substring
        // match — full-match semantics mean only the exact `v2.0.0` qualifies.
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.Ok,
                new[] { "v2.0.0", "v2.1.0-rc1" }, null, DateTime.UtcNow));
        registry.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), "v2.0.0",
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"v\d+\.\d+\.\d+"));

        // v2.1.0-rc1 is excluded → highest full match is v2.0.0 (== running).
        Assert.Equal("v2.0.0", result.LatestVersionTag);
        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task CheckAsync_FilterSet_NoMatchingTag_ReturnsUpToDateWithExplanation()
    {
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.Ok,
                new[] { "nightly", "edge" }, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"^v\d+\.\d+\.\d+$"));

        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
        Assert.Null(result.LatestDigest);
        Assert.Contains("No tags matched", result.Error);
        // Should not have called GetManifestDigestAsync since no candidate.
        registry.Verify(r => r.GetManifestDigestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_FilterSet_TagListingFails_ReturnsError()
    {
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.ListTagsAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryTagListResult(RegistryManifestStatus.RateLimited,
                Array.Empty<string>(), "rate-limited", DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: @"^v\d+\.\d+\.\d+$"));

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Equal(DigestA, result.CurrentDigest);
        Assert.Contains("rate-limited", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_FilterSet_InvalidRegex_ReturnsError()
    {
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: "[unclosed"));

        Assert.Equal(DockerUpdateStatus.Error, result.Status);
        Assert.Contains("Invalid tag pattern", result.Error);
        // We should not even attempt to list tags when the regex is unparseable.
        registry.Verify(r => r.ListTagsAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_FilterNullOrWhitespace_PreservesV1Behaviour()
    {
        var host = MockHost(new DockerHostResult(
            DockerHostStatus.Ok, DigestA, "owner/repo@" + DigestA, "sha256:img", null));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), "latest",
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.CheckAsync(Profile(tagPatternFilter: "   "));

        Assert.Equal(DockerUpdateStatus.UpToDate, result.Status);
        // Whitespace-only filter must NOT trigger the tag-listing path.
        registry.Verify(r => r.ListTagsAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RegistryAuthContext>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── TestConnectionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_AllChecksPass_ReturnsAllTrue()
    {
        var host = MockHostConnection(new DockerHostConnectionResult(true, true, null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.TestConnectionAsync(Profile());

        Assert.True(result.DockerHostReachable);
        Assert.True(result.ContainerFound);
        Assert.True(result.RegistryReachable);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestConnectionAsync_RegistryNotFound_StillCountsAsReachable()
    {
        // 404 from the registry means the registry IS reachable — the tag just doesn't exist.
        // We surface that as Reachable=true so the UI can distinguish "wrong tag" from "no network".
        var host = MockHostConnection(new DockerHostConnectionResult(true, true, null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.NotFound, null, null, null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.TestConnectionAsync(Profile());

        Assert.True(result.RegistryReachable);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestConnectionAsync_RegistryNetworkError_ReturnsRegistryUnreachable()
    {
        var host = MockHostConnection(new DockerHostConnectionResult(true, true, null));
        var registry = MockRegistry(new RegistryManifestResult(RegistryManifestStatus.NetworkError, null, null, "dns", DateTime.UtcNow));
        var checker = BuildChecker(host, registry);

        var result = await checker.TestConnectionAsync(Profile());

        Assert.True(result.DockerHostReachable);
        Assert.False(result.RegistryReachable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task TestConnectionAsync_HostUnreachable_StillCallsRegistry()
    {
        // The two checks are independent — surfacing both states is more useful
        // than short-circuiting on the first failure.
        var host = MockHostConnection(new DockerHostConnectionResult(false, false, "no daemon"));
        var registry = new Mock<IRegistryClient>();
        registry.Setup(r => r.GetManifestDigestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryManifestResult(RegistryManifestStatus.Ok, DigestA, null, null, DateTime.UtcNow));

        var checker = BuildChecker(host, registry);

        var result = await checker.TestConnectionAsync(Profile());

        Assert.False(result.DockerHostReachable);
        Assert.True(result.RegistryReachable);
        Assert.Contains("no daemon", result.Error);
    }

    // ── V2.3: GitHub Releases enrichment ─────────────────────────────────────

    [Fact]
    public async Task CheckAsync_GhcrUpdateAvailable_FetchesGitHubReleaseAndPopulatesEnrichmentFields()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA,
            "ghcr.io/owner/repo@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(
            RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var gh = new Mock<IGitHubReleaseClient>();
        gh.Setup(g => g.GetReleaseByTagAsync(
                "owner", "repo", "v2.0.0",
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubReleaseResult(
                GitHubReleaseStatus.Ok,
                "https://github.com/owner/repo/releases/tag/v2.0.0",
                "## v2.0.0\n- shiny new feature",
                null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry, gh);

        var result = await checker.CheckAsync(GhcrProfile(tag: "v2.0.0"));

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.0.0", result.LatestReleaseUrl);
        Assert.Contains("shiny new feature", result.LatestReleaseBody);
    }

    [Fact]
    public async Task CheckAsync_GhcrButGitHubReturns404_DoesNotFailParentStatus()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA,
            "ghcr.io/owner/repo@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(
            RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var gh = new Mock<IGitHubReleaseClient>();
        gh.Setup(g => g.GetReleaseByTagAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubReleaseResult(
                GitHubReleaseStatus.NotFound, null, null,
                "No release for this tag.", DateTime.UtcNow));
        var checker = BuildChecker(host, registry, gh);

        var result = await checker.CheckAsync(GhcrProfile(tag: "v2.0.0"));

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Null(result.LatestReleaseUrl);
        Assert.Null(result.LatestReleaseBody);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_NonGhcrRegistry_SkipsGitHubLookupEntirely()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA,
            "library/nginx@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(
            RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var gh = new Mock<IGitHubReleaseClient>(MockBehavior.Strict);
        var checker = BuildChecker(host, registry, gh);

        var result = await checker.CheckAsync(Profile());

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        gh.VerifyNoOtherCalls(); // strict mock with no setup → would throw if called
    }

    [Fact]
    public async Task CheckAsync_GhcrPassesPatToReleaseClient()
    {
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA,
            "ghcr.io/owner/repo@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(
            RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var gh = new Mock<IGitHubReleaseClient>();
        gh.Setup(g => g.GetReleaseByTagAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                "ghp_supersecret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubReleaseResult(
                GitHubReleaseStatus.Ok,
                "https://github.com/owner/repo/releases/tag/v1.0",
                "body", null, DateTime.UtcNow));
        var checker = BuildChecker(host, registry, gh);

        var profile = GhcrProfile(tag: "v1.0") with { GitHubPat = "ghp_supersecret" };
        var result = await checker.CheckAsync(profile);

        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.0", result.LatestReleaseUrl);
        gh.Verify(g => g.GetReleaseByTagAsync(
            "owner", "repo", "v1.0", "ghp_supersecret", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAsync_GhcrWithNestedRepositoryPath_SkipsReleaseLookup()
    {
        // `org/team/project` doesn't map cleanly to GitHub's flat owner/repo
        // model — the orchestrator should bail rather than 404 every check.
        var host = MockHost(new DockerHostResult(DockerHostStatus.Ok, DigestA,
            "ghcr.io/org/team/project@" + DigestA, "sha256:img", null));
        var registry = MockRegistry(new RegistryManifestResult(
            RegistryManifestStatus.Ok, DigestB, null, null, DateTime.UtcNow));
        var gh = new Mock<IGitHubReleaseClient>(MockBehavior.Strict);
        var checker = BuildChecker(host, registry, gh);

        var profile = GhcrProfile(tag: "v1.0") with { Repository = "org/team/project" };
        var result = await checker.CheckAsync(profile);

        Assert.Equal(DockerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Null(result.LatestReleaseUrl);
        gh.VerifyNoOtherCalls();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DockerWatchProfile Profile(string? tagPatternFilter = null) =>
        new(
            "library/nginx:latest",
            "docker.io",
            "library/nginx",
            "latest",
            DockerHostType.LocalSocket,
            null,
            "nginx-container",
            null,
            null,
            tagPatternFilter);

    private static DockerWatchProfile GhcrProfile(string tag) =>
        new(
            $"ghcr.io/owner/repo:{tag}",
            "ghcr.io",
            "owner/repo",
            tag,
            DockerHostType.LocalSocket,
            null,
            "svc",
            null,
            null,
            null);

    private static Mock<IDockerHostClient> MockHost(DockerHostResult result)
    {
        var mock = new Mock<IDockerHostClient>();
        mock.Setup(h => h.GetCurrentImageDigestAsync(
                It.IsAny<DockerHostTransport>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static Mock<IDockerHostClient> MockHostConnection(DockerHostConnectionResult result)
    {
        var mock = new Mock<IDockerHostClient>();
        mock.Setup(h => h.TestContainerAsync(
                It.IsAny<DockerHostTransport>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static Mock<IRegistryClient> MockRegistry(RegistryManifestResult result)
    {
        var mock = new Mock<IRegistryClient>();
        mock.Setup(r => r.GetManifestDigestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<RegistryAuthContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static DockerUpdateChecker BuildChecker(
        Mock<IDockerHostClient> host,
        Mock<IRegistryClient> registry,
        Mock<IGitHubReleaseClient>? gitHub = null)
    {
        // V2.3 — default GitHub mock returns NotFound so existing
        // non-GHCR-aware tests keep producing release-free results.
        var releaseClient = gitHub ?? new Mock<IGitHubReleaseClient>();
        if (gitHub is null)
        {
            releaseClient.Setup(g => g.GetReleaseByTagAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitHubReleaseResult(
                    GitHubReleaseStatus.NotFound, null, null, null, DateTime.UtcNow));
        }
        return new DockerUpdateChecker(
            host.Object, registry.Object, releaseClient.Object,
            NullLogger<DockerUpdateChecker>.Instance);
    }
}
