using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V3.1 — <c>GET /watches/{id}/inspect</c> ("Container Inspect viewer").
/// Covers the happy path, the error envelopes the controller projects onto
/// HTTP status codes, and the owner-only access check.
/// </summary>
public class InspectEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Inspect_ReturnsInspectPayload_OnSuccess()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "wp");

        var payload = BuildInspect(name: "wp", image: "ghcr.io/owner/repo:v1");
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), "wp", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerInspectResult(DockerHostStatus.Ok, payload, null));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerContainerInspect>(ok.Value);
        Assert.Equal("wp", response.Name);
        Assert.Equal("ghcr.io/owner/repo:v1", response.Image);
    }

    [Fact]
    public async Task Inspect_Returns404_WhenContainerMissingOnHost()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "ghost");
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), "ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerInspectResult(
                DockerHostStatus.ContainerNotFound, null, "Container 'ghost' not found on the configured Docker host."));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, watch.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Inspect_Returns502_WhenHostUnreachable()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerContainerInspectResult(DockerHostStatus.HostUnreachable, null, "dns fail"));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, watch.Id, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task Inspect_Returns502_WhenHostClientThrows()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        _hostClientMock
            .Setup(h => h.InspectContainerAsync(It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connect timed out"));

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, watch.Id, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Fact]
    public async Task Inspect_Returns404_WhenWatchBelongsToAnotherUser()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var watch = await SeedWatchAsync(svc.Id, _otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, watch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _hostClientMock.Verify(h => h.InspectContainerAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Inspect_Returns404_WhenWatchDoesNotExist()
    {
        var svc = await _dataFactory.ServiceAsync();

        var ctrl = BuildController();
        var result = await ctrl.Inspect(svc.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private const string FakeImageId = "sha256:cafebabe";

    private static DockerContainerInspect BuildInspect(string name, string image) =>
        new(
            Id: "abcd1234",
            Name: name,
            Image: image,
            ImageId: FakeImageId,
            ImageRepoDigests: Array.Empty<string>(),
            CreatedUtc: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            RestartCount: 0,
            Platform: "linux",
            Driver: "overlay2",
            State: new DockerInspectState(
                Status: "running",
                Running: true, Restarting: false, Paused: false,
                OomKilled: false, Dead: false, ExitCode: 0,
                Error: null,
                StartedUtc: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                FinishedUtc: null, Health: null),
            Config: new DockerInspectConfig(
                Hostname: null, User: null, WorkingDir: null, Image: image,
                Entrypoint: Array.Empty<string>(), Cmd: Array.Empty<string>(),
                Env: Array.Empty<DockerInspectEnvVar>(),
                Labels: new Dictionary<string, string>(),
                ExposedPorts: Array.Empty<string>()),
            HostConfig: new DockerInspectHostConfig(
                NetworkMode: "bridge", RestartPolicy: null,
                MemoryBytes: null, CpuShares: null,
                Privileged: false, ReadonlyRootfs: false, AutoRemove: false,
                PortBindings: Array.Empty<DockerInspectPortBinding>()),
            NetworkSettings: new DockerInspectNetworkSettings(new Dictionary<string, DockerInspectNetwork>()),
            Mounts: Array.Empty<DockerInspectMount>());
}



