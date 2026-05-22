using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Stashboard.Tests.Controllers.DockerWatches;

public class DeleteWatchEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Delete_RemovesWatch_AndReturnsNoContent()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Delete(svc.Id, watch.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.Id == watch.Id));
        Assert.True(await _dbContext.WebResources.AnyAsync(s => s.Id == svc.Id));
    }

    [Fact]
    public async Task Delete_OnlyRemovesTheTargetedWatch_NotSiblings()
    {
        var svc = await _dataFactory.ServiceAsync();
        var appWatch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var dbWatch = await SeedWatchAsync(svc.Id, _userId, label: "db", containerName: "wp-db");
        var ctrl = BuildController();

        await ctrl.Delete(svc.Id, appWatch.Id, CancellationToken.None);

        var remaining = await ReloadWatchesByServiceAsync(svc.Id);
        Assert.Single(remaining);
        Assert.Equal(dbWatch.Id, remaining[0].Id);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenWatchDoesNotExist()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.Delete(svc.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Delete(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(await _dbContext.DockerWatches.AnyAsync(w => w.Id == foreignWatch.Id));
    }
}
