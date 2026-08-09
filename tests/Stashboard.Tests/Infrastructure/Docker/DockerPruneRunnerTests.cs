using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V5.5 — unit tests for <see cref="DockerPruneRunner"/>. The runner is a
/// thin envelope around <see cref="IDockerHostClient.PruneImagesAsync"/>:
/// these tests pin down the four outcomes that matter to the audit row
/// (Success / NothingToPrune / HostUnreachable / Failed) and the
/// crashed-with-exception fallback.
/// </summary>
public class DockerPruneRunnerTests
{
    private static DockerPruneRequest Request(bool includeUnused = false) =>
        new(
            ConnectionId: Guid.NewGuid(),
            Transport: new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            IncludeUnused: includeUnused,
            Trigger: DockerPruneTrigger.Manual,
            InitiatedByUserId: Guid.NewGuid());

    [Fact]
    public async Task Run_ReclaimedBytes_MapsToSuccess()
    {
        var (runner, host) = Build();
        host.Setup(h => h.PruneImagesAsync(It.IsAny<DockerHostTransport>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerImagePruneResult(DockerHostStatus.Ok, 3, 1_000_000, null));

        var result = await runner.RunAsync(Request());

        Assert.Equal(DockerPruneStatus.Success, result.Status);
        Assert.Equal(3, result.ImagesDeleted);
        Assert.Equal(1_000_000, result.SpaceReclaimedBytes);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Run_ZeroDeletedZeroBytes_MapsToNothingToPrune()
    {
        var (runner, host) = Build();
        host.Setup(h => h.PruneImagesAsync(It.IsAny<DockerHostTransport>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerImagePruneResult(DockerHostStatus.Ok, 0, 0, null));

        var result = await runner.RunAsync(Request());

        Assert.Equal(DockerPruneStatus.NothingToPrune, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Run_HostUnreachable_MapsAndCarriesError()
    {
        var (runner, host) = Build();
        host.Setup(h => h.PruneImagesAsync(It.IsAny<DockerHostTransport>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerImagePruneResult(DockerHostStatus.HostUnreachable, 0, 0, "dns: NXDOMAIN"));

        var result = await runner.RunAsync(Request());

        Assert.Equal(DockerPruneStatus.HostUnreachable, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal("dns: NXDOMAIN", result.Error);
    }

    [Fact]
    public async Task Run_ForwardsIncludeUnusedToHostClient()
    {
        var (runner, host) = Build();
        host.Setup(h => h.PruneImagesAsync(It.IsAny<DockerHostTransport>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerImagePruneResult(DockerHostStatus.Ok, 5, 1234, null))
            .Verifiable();

        var result = await runner.RunAsync(Request(includeUnused: true));

        Assert.True(result.IncludedUnused);
        host.Verify();
    }

    [Fact]
    public async Task Run_Crash_MapsToFailedWithExceptionMessage()
    {
        var (runner, host) = Build();
        host.Setup(h => h.PruneImagesAsync(It.IsAny<DockerHostTransport>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await runner.RunAsync(Request());

        Assert.Equal(DockerPruneStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error);
    }

    private static (DockerPruneRunner Runner, Mock<IDockerHostClient> Host) Build()
    {
        var host = new Mock<IDockerHostClient>();
        var runner = new DockerPruneRunner(host.Object, TimeProvider.System, NullLogger<DockerPruneRunner>.Instance);
        return (runner, host);
    }
}



