using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

public class UpdateWatchEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Update_PreservesIdAndPersistsChanges()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", imageReference: "nginx:1.27",
            registryHost: "docker.io", repository: "library/nginx", tag: "1.27");
        var ctrl = BuildController();
        var request = CreateWatchEndpointTests.DefaultRequest(label: "app", image: "nginx:1.28");

        var result = await ctrl.Update(svc.Id, watch.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(watch.Id, response.Id);
        Assert.Equal("1.28", response.Tag);
    }

    [Fact]
    public async Task Update_ChangingContainerToOneAlreadyTrackedOnSameConnection_ReturnsConflict()
    {
        // V3.6 — the natural key is (connection, container). Both watches share
        // the service's connection, so pointing "db" at the app container
        // collides.
        var svc = await _dataFactory.ServiceAsync();
        await SeedWatchAsync(svc.Id, _userId, label: "app", containerName: "wp-app");
        var dbWatch = await SeedWatchAsync(svc.Id, _userId, label: "db", containerName: "wp-db");
        var ctrl = BuildController();

        // Try to repoint "db" at the already-tracked "wp-app" container.
        var request = CreateWatchEndpointTests.DefaultRequest(label: "db", image: "mariadb:11", containerName: "wp-app");
        var result = await ctrl.Update(svc.Id, dbWatch.Id, request, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_TelegramNotificationsToggle_RoundTrips()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var ctrl = BuildController();
        var request = CreateWatchEndpointTests.DefaultRequest() with { TelegramNotificationsEnabled = true };

        await ctrl.Update(svc.Id, watch.Id, request, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.True(dbRow!.TelegramNotificationsEnabled);
    }

    [Fact]
    public async Task Update_KeepActionPreservesStoredEncryptedValue()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", withRegistryCreds: true);
        var ctrl = BuildController();
        var request = CreateWatchEndpointTests.DefaultRequest() with
        {
            RegistryUsername = new SecretValueUpsert(SecretValueAction.Keep, null),
            RegistryPassword = new SecretValueUpsert(SecretValueAction.Keep, null),
        };

        await ctrl.Update(svc.Id, watch.Id, request, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal("enc:user", dbRow!.RegistryUsernameEncrypted);
        Assert.Equal("enc:pass", dbRow.RegistryPasswordEncrypted);
    }

    [Fact]
    public async Task Update_ClearActionDropsStoredSecret()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", withRegistryCreds: true);
        var ctrl = BuildController();
        var request = CreateWatchEndpointTests.DefaultRequest() with
        {
            RegistryUsername = new SecretValueUpsert(SecretValueAction.Clear, null),
            RegistryPassword = new SecretValueUpsert(SecretValueAction.Clear, null),
        };

        await ctrl.Update(svc.Id, watch.Id, request, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Null(dbRow!.RegistryUsernameEncrypted);
        Assert.Null(dbRow.RegistryPasswordEncrypted);
    }

    [Fact]
    public async Task Update_ReturnsBadRequest_WhenImageReferenceMalformed()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Update(svc.Id, watch.Id, CreateWatchEndpointTests.DefaultRequest(image: "@@@bad"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenWatchMissing()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.Update(svc.Id, Guid.NewGuid(), CreateWatchEndpointTests.DefaultRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_TagPatternFilter_NullClearsExistingValue()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        // Seed an existing filter so we can verify Update sets it back to null.
        var tracked = await _dbContext.DockerWatches.AsTracking().SingleAsync(w => w.Id == watch.Id);
        tracked.TagPatternFilter = "^old$";
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var request = CreateWatchEndpointTests.DefaultRequest(tagPatternFilter: null);

        await ctrl.Update(svc.Id, watch.Id, request, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Null(dbRow!.TagPatternFilter);
    }

    [Fact]
    public async Task Update_TagPatternFilter_InvalidRegex_ReturnsBadRequest()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Update(svc.Id, watch.Id,
            CreateWatchEndpointTests.DefaultRequest(tagPatternFilter: "[unclosed"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Update(foreignSvc.Id, foreignWatch.Id, CreateWatchEndpointTests.DefaultRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}



