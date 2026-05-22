using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

public class GetWatchEndpointTests : DockerWatchesControllerTestBase
{
    // ── List ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsEmptyArray_WhenServiceHasNoWatches()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.List(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<List<DockerWatchResponse>>(ok.Value);
        Assert.Empty(response);
    }

    [Fact]
    public async Task List_ReturnsAllWatches_OrderedByLabel()
    {
        var svc = await _dataFactory.ServiceAsync();
        await SeedWatchAsync(svc.Id, _userId, label: "db", containerName: "wp-db");
        await SeedWatchAsync(svc.Id, _userId, label: "app", containerName: "wp-app");
        var ctrl = BuildController();

        var result = await ctrl.List(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<List<DockerWatchResponse>>(ok.Value);
        Assert.Equal(2, response.Count);
        Assert.Equal("app", response[0].Label);
        Assert.Equal("db", response[1].Label);
    }

    [Fact]
    public async Task List_ReturnsNotFound_WhenServiceMissing()
    {
        var ctrl = BuildController();
        var result = await ctrl.List(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.List(foreignSvc.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Get one ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsOk_WhenWatchExists()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", status: DockerUpdateStatus.UpToDate);
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(watch.Id, response.Id);
        Assert.Equal("app", response.Label);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenWatchIdBelongsToAnotherService()
    {
        // Two services on the same user — make sure watchId scoping by service works.
        var ownSvc = await _dataFactory.ServiceAsync(name: "Own");
        var otherSvc = await _dataFactory.ServiceAsync(name: "Other");
        var watch = await SeedWatchAsync(otherSvc.Id, _userId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Get(ownSvc.Id, watch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Get(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_HasRegistryCredentialsFlag_ReflectsStoredSecrets()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", withRegistryCreds: true);
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.True(response.HasRegistryCredentials);
    }
}
