using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// Exercises <see cref="DockerWatchesController.TestWatch"/> — the per-watch
/// reachability probe. The connection's transport is read straight from the
/// persisted <c>DockerConnectionEntity</c>; this endpoint only resolves
/// tri-state registry credentials and forwards to the orchestrator.
/// </summary>
public class TestConnectionEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task TestWatch_AllChecksPass_ReturnsAllTrue()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();
        _updateCheckerMock
            .Setup(c => c.TestConnectionAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerConnectionTestResult(true, true, true, null));

        var result = await ctrl.TestWatch(svc.Id, DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchTestResponse>(ok.Value);
        Assert.True(response.DockerHostReachable);
        Assert.True(response.ContainerFound);
        Assert.True(response.RegistryReachable);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task TestWatch_RegistryUnreachable_SurfacesPartialFailure()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();
        _updateCheckerMock
            .Setup(c => c.TestConnectionAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerConnectionTestResult(true, true, false, "Registry unreachable: dns"));

        var result = await ctrl.TestWatch(svc.Id, DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchTestResponse>(ok.Value);
        Assert.True(response.DockerHostReachable);
        Assert.False(response.RegistryReachable);
        Assert.Contains("dns", response.Error);
    }

    [Fact]
    public async Task TestWatch_WatchIdSupplied_ResolvesKeptSecretsFromExistingWatch()
    {
        var svc = await _dataFactory.ServiceAsync();
        var existing = await SeedWatchAsync(svc.Id, _userId, label: "app", withRegistryCreds: true);
        DockerWatchProfile? captured = null;
        _updateCheckerMock
            .Setup(c => c.TestConnectionAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .Callback<DockerWatchProfile, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new DockerConnectionTestResult(true, true, true, null));

        var ctrl = BuildController();
        var request = DefaultRequest("ghcr.io/owner/repo:v2") with
        {
            RegistryUsername = new SecretValueUpsert(SecretValueAction.Keep, null),
            RegistryPassword = new SecretValueUpsert(SecretValueAction.Keep, null),
        };

        await ctrl.TestWatch(svc.Id, request, watchId: existing.Id, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.RegistryCredentials);
        Assert.Equal("user", captured.RegistryCredentials!.Username);
        Assert.Equal("pass", captured.RegistryCredentials.Password);
    }

    [Fact]
    public async Task TestWatch_NoWatchId_KeepActionYieldsNoSecrets_ForAddAnotherFlow()
    {
        var svc = await _dataFactory.ServiceAsync();
        // Sibling watch on the same service with creds — should NOT be picked up
        // since we didn't supply its id.
        await SeedWatchAsync(svc.Id, _userId, label: "sibling", withRegistryCreds: true);
        DockerWatchProfile? captured = null;
        _updateCheckerMock
            .Setup(c => c.TestConnectionAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .Callback<DockerWatchProfile, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new DockerConnectionTestResult(true, true, true, null));

        var ctrl = BuildController();
        var request = DefaultRequest() with
        {
            RegistryUsername = new SecretValueUpsert(SecretValueAction.Keep, null),
            RegistryPassword = new SecretValueUpsert(SecretValueAction.Keep, null),
        };

        await ctrl.TestWatch(svc.Id, request, watchId: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.RegistryCredentials);
    }

    [Fact]
    public async Task TestWatch_DoesNotPersistAnything()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        _updateCheckerMock
            .Setup(c => c.TestConnectionAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerConnectionTestResult(true, true, true, null));
        var ctrl = BuildController();

        await ctrl.TestWatch(svc.Id, DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        Assert.Empty(await ReloadWatchesByServiceAsync(svc.Id));
    }

    [Fact]
    public async Task TestWatch_ReturnsBadRequest_WhenImageReferenceMalformed()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.TestWatch(svc.Id, DefaultRequest("@@@bad"), watchId: null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _updateCheckerMock.Verify(c => c.TestConnectionAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestWatch_ReturnsBadRequest_WhenNoConnectionExistsForService()
    {
        // Connection must exist before a watch test can run — refusing here
        // gives the UI a clear "configure the daemon first" signal.
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.TestWatch(svc.Id, DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _updateCheckerMock.Verify(c => c.TestConnectionAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestWatch_ReturnsNotFound_WhenServiceDoesNotExist()
    {
        var ctrl = BuildController();

        var result = await ctrl.TestWatch(Guid.NewGuid(), DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _updateCheckerMock.Verify(c => c.TestConnectionAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestWatch_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.TestWatch(foreignSvc.Id, DefaultRequest("nginx:latest"), watchId: null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _updateCheckerMock.Verify(c => c.TestConnectionAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DockerWatchTestRequest DefaultRequest(string imageReference = "nginx:latest") =>
        new(
            ImageReference: imageReference,
            ContainerName: "svc",
            RegistryUsername: null,
            RegistryPassword: null);
}
