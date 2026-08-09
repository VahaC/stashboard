using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Phase 2 definition-of-done: an integration test against the real Docker Hub
/// registry that resolves a non-null digest for <c>library/nginx:latest</c>.
///
/// Disabled by default — set <c>STASHBOARD_RUN_NETWORK_TESTS=1</c> to run.
/// Skipped on CI so a Docker Hub rate-limit hit doesn't redden the build.
/// </summary>
public class OciRegistryClientIntegrationTests
{
    private const string GateEnvironmentVariable = "STASHBOARD_RUN_NETWORK_TESTS";

    [Fact]
    public async Task GetManifestDigest_RealDockerHub_LibraryNginxLatest_ResolvesDigest()
    {
        if (Environment.GetEnvironmentVariable(GateEnvironmentVariable) != "1")
            return; // opt-in only — see class summary

        var factory = new SingleClientFactory(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        var client = new OciRegistryClient(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            new Mock<IAwsEcrTokenProvider>(MockBehavior.Strict).Object,
            NullLogger<OciRegistryClient>.Instance);

        var result = await client.GetManifestDigestAsync("docker.io", "library/nginx", "latest", credentials: (RegistryCredentials?)null);

        Assert.Equal(Stashboard.Core.Abstractions.RegistryManifestStatus.Ok, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Digest));
        Assert.StartsWith("sha256:", result.Digest);
    }

    [Fact]
    public async Task GetManifestDigest_RealGhcr_AnonymousPublicImage_ResolvesDigest()
    {
        if (Environment.GetEnvironmentVariable(GateEnvironmentVariable) != "1")
            return;

        var factory = new SingleClientFactory(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        var client = new OciRegistryClient(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            new Mock<IAwsEcrTokenProvider>(MockBehavior.Strict).Object,
            NullLogger<OciRegistryClient>.Instance);

        // homeassistant/home-assistant is a widely-used public image on GHCR with stable tags.
        var result = await client.GetManifestDigestAsync(
            "ghcr.io", "home-assistant/home-assistant", "stable", credentials: (RegistryCredentials?)null);

        Assert.Equal(Stashboard.Core.Abstractions.RegistryManifestStatus.Ok, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Digest));
        Assert.StartsWith("sha256:", result.Digest);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}



