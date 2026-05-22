using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Api.Contracts;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V2.6 — rotate + delete endpoints for the per-watch webhook token. The
/// receiver itself is tested in <c>DockerWebhooksControllerTests</c>;
/// these tests cover the owner-side management surface.
/// </summary>
public class WebhookTokenEndpointTests : DockerWatchesControllerTestBase
{
    private static readonly string FirstToken = new('a', 64);
    private static readonly string SecondToken = new('b', 64);

    [Fact]
    public async Task Rotate_FirstCall_StoresGeneratedTokenOnEntity()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        _webhookTokenGeneratorMock.Setup(g => g.Generate()).Returns(FirstToken);

        var ctrl = BuildController();
        var result = await ctrl.RotateWebhookToken(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(FirstToken, response.WebhookToken);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(FirstToken, dbRow!.WebhookToken);
    }

    [Fact]
    public async Task Rotate_SecondCall_ReplacesTokenAndInvalidatesPrevious()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        _webhookTokenGeneratorMock.SetupSequence(g => g.Generate())
            .Returns(FirstToken)
            .Returns(SecondToken);

        var ctrl = BuildController();
        await ctrl.RotateWebhookToken(svc.Id, watch.Id, CancellationToken.None);
        var second = await ctrl.RotateWebhookToken(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(second.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(SecondToken, response.WebhookToken);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(SecondToken, dbRow!.WebhookToken);
    }

    [Fact]
    public async Task Rotate_RetriesOnCollisionWithDifferentWatch()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");

        // Pre-seed a second watch (on a different service) with FirstToken so
        // the generator's first attempt collides with the unique index. The
        // controller should retry and pick SecondToken on the second attempt.
        var otherSvc = await _dataFactory.ServiceAsync(name: "Other");
        var otherWatch = await SeedWatchAsync(otherSvc.Id, _userId, label: "app");
        otherWatch.WebhookToken = FirstToken;
        _dbContext.DockerWatches.Update(otherWatch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _webhookTokenGeneratorMock.SetupSequence(g => g.Generate())
            .Returns(FirstToken)
            .Returns(SecondToken);

        var ctrl = BuildController();
        var result = await ctrl.RotateWebhookToken(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(SecondToken, response.WebhookToken);
    }

    [Fact]
    public async Task Rotate_ForeignWatch_ReturnsNotFound()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.RotateWebhookToken(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_ClearsTokenAndLastReceived()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        watch.WebhookToken = FirstToken;
        watch.LastWebhookReceivedUtc = DateTime.UtcNow;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var result = await ctrl.DeleteWebhookToken(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Null(response.WebhookToken);
        Assert.Null(response.LastWebhookReceivedUtc);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Null(dbRow!.WebhookToken);
        Assert.Null(dbRow.LastWebhookReceivedUtc);
    }

    [Fact]
    public async Task Delete_WhenNoTokenSet_ReturnsOkWithoutChanges()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.DeleteWebhookToken(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Null(response.WebhookToken);
    }
}
