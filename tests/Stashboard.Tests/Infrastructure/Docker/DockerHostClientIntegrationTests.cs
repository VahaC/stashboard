using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Phase 3 definition-of-done: against the dev machine's local Docker socket,
/// reads the RepoDigest of the running <c>stashboard-db-dev</c> container.
///
/// Disabled by default. To run:
///   <c>STASHBOARD_RUN_LOCAL_DOCKER_TESTS=1 dotnet test</c>
/// and ensure <c>docker compose -f docker-compose.dev.yml up -d</c> has been
/// invoked at least once so the container exists.
/// </summary>
public class DockerHostClientIntegrationTests
{
    private const string GateEnvironmentVariable = "STASHBOARD_RUN_LOCAL_DOCKER_TESTS";

    [Fact]
    public async Task GetCurrentImageDigest_LocalSocket_StashboardDbDev_ResolvesDigest()
    {
        if (Environment.GetEnvironmentVariable(GateEnvironmentVariable) != "1")
            return;

        var client = new DockerHostClient(
            new DockerClientFactory(),
            new ImageReferenceParser(),
            NullLogger<DockerHostClient>.Instance);

        // The dev compose file pins postgres:16-alpine for this container.
        var result = await client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            containerName: "stashboard-db-dev",
            registryHost: "docker.io",
            repository: "library/postgres");

        Assert.Equal(DockerHostStatus.Ok, result.Status);
        Assert.NotNull(result.Digest);
        Assert.StartsWith("sha256:", result.Digest);
        Assert.NotNull(result.ImageId);
        Assert.StartsWith("sha256:", result.ImageId);
        Assert.NotNull(result.MatchedRepoDigest);
        Assert.Contains("postgres", result.MatchedRepoDigest);
    }

    [Fact]
    public async Task TestConnection_LocalSocket_StashboardDbDev_Succeeds()
    {
        if (Environment.GetEnvironmentVariable(GateEnvironmentVariable) != "1")
            return;

        var client = new DockerHostClient(
            new DockerClientFactory(),
            new ImageReferenceParser(),
            NullLogger<DockerHostClient>.Instance);

        var result = await client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            containerName: "stashboard-db-dev");

        Assert.True(result.HostReachable);
        Assert.True(result.ContainerFound);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestConnection_LocalSocket_UnknownContainer_HostReachableContainerNotFound()
    {
        if (Environment.GetEnvironmentVariable(GateEnvironmentVariable) != "1")
            return;

        var client = new DockerHostClient(
            new DockerClientFactory(),
            new ImageReferenceParser(),
            NullLogger<DockerHostClient>.Instance);

        var result = await client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            containerName: $"definitely-does-not-exist-{Guid.NewGuid():N}");

        Assert.True(result.HostReachable);
        Assert.False(result.ContainerFound);
        Assert.NotNull(result.Error);
    }
}
