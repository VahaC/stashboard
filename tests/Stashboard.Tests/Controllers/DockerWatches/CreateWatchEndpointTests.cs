using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

public class CreateWatchEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Create_PersistsWatch_AndReturns201()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(svc.Id, DefaultRequest(label: "app", image: "ghcr.io/owner/repo:latest"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(svc.Id, response.WebResourceId);
        Assert.Equal("app", response.Label);
        Assert.Equal("ghcr.io", response.RegistryHost);
        Assert.Equal("owner/repo", response.Repository);
        Assert.Equal("latest", response.Tag);

        var dbRow = await ReloadWatchAsync(response.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(_userId, dbRow!.UserId);
    }

    [Fact]
    public async Task Create_AllowsMultipleWatchesPerService_WhenLabelsDiffer()
    {
        var svc = await _dataFactory.ServiceAsync(name: "WordPress");
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        await ctrl.Create(svc.Id, DefaultRequest(label: "app", image: "wordpress:latest", containerName: "wp-app"), CancellationToken.None);
        await ctrl.Create(svc.Id, DefaultRequest(label: "db", image: "mariadb:11", containerName: "wp-db"), CancellationToken.None);

        var stored = await ReloadWatchesByServiceAsync(svc.Id);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, w => w.Label == "app" && w.RegistryHost == "docker.io");
        Assert.Contains(stored, w => w.Label == "db");
    }

    [Fact]
    public async Task Create_DuplicateLabelOnSameService_ReturnsConflict()
    {
        var svc = await _dataFactory.ServiceAsync();
        await SeedWatchAsync(svc.Id, _userId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Create(svc.Id, DefaultRequest(label: "app", image: "nginx:latest"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_SameLabelOnDifferentServices_AllowedForSameUser()
    {
        var svcA = await _dataFactory.ServiceAsync(name: "A");
        var svcB = await _dataFactory.ServiceAsync(name: "B");
        await EnsureConnectionAsync(svcA.Id, _userId);
        await EnsureConnectionAsync(svcB.Id, _userId);
        var ctrl = BuildController();

        await ctrl.Create(svcA.Id, DefaultRequest(label: "app", image: "nginx:latest"), CancellationToken.None);
        var result = await ctrl.Create(svcB.Id, DefaultRequest(label: "app", image: "nginx:latest"), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenImageReferenceMalformed()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(svc.Id, DefaultRequest(image: "@@@bad"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenNoConnectionConfigured()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.Create(svc.Id, DefaultRequest(image: "nginx:latest"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenServiceDoesNotExist()
    {
        var ctrl = BuildController();
        var result = await ctrl.Create(Guid.NewGuid(), DefaultRequest(image: "nginx:latest"), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.Create(foreignSvc.Id, DefaultRequest(image: "nginx:latest"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.WebResourceId == foreignSvc.Id));
    }

    [Fact]
    public async Task Create_DailySchedule_RoundTripsThroughResponseAndPersists()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest",
                scheduleType: CheckScheduleType.Daily,
                checkAtTime: new TimeOnly(8, 0)),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(CheckScheduleType.Daily, response.ScheduleType);
        Assert.Equal(new TimeOnly(8, 0), response.CheckAtTime);
        Assert.Null(response.CheckOnDayOfWeek);

        var dbRow = await ReloadWatchAsync(response.Id);
        Assert.Equal(CheckScheduleType.Daily, dbRow!.ScheduleType);
        Assert.Equal(new TimeOnly(8, 0), dbRow.CheckAtTime);
    }

    [Fact]
    public async Task Create_DailyScheduleMissingTime_ReturnsBadRequest()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest",
                scheduleType: CheckScheduleType.Daily,
                checkAtTime: null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Create_WeeklySchedule_RoundTripsDayAndTime()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest",
                scheduleType: CheckScheduleType.Weekly,
                checkAtTime: new TimeOnly(9, 30),
                checkOnDayOfWeek: DayOfWeek.Monday),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(CheckScheduleType.Weekly, response.ScheduleType);
        Assert.Equal(DayOfWeek.Monday, response.CheckOnDayOfWeek);
        Assert.Equal(new TimeOnly(9, 30), response.CheckAtTime);
    }

    [Fact]
    public async Task Create_HourlyDisallowedHours_ReturnsBadRequest()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest", checkEveryHours: 3),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_TagPatternFilter_RoundTripsThroughResponseAndPersists()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest", tagPatternFilter: @"^v\d+\.\d+\.\d+$"),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(@"^v\d+\.\d+\.\d+$", response.TagPatternFilter);

        var dbRow = await ReloadWatchAsync(response.Id);
        Assert.Equal(@"^v\d+\.\d+\.\d+$", dbRow!.TagPatternFilter);
    }

    [Fact]
    public async Task Create_TagPatternFilter_InvalidRegex_ReturnsBadRequest()
    {
        var svc = await _dataFactory.ServiceAsync();
        await EnsureConnectionAsync(svc.Id, _userId);
        var ctrl = BuildController();

        var result = await ctrl.Create(
            svc.Id,
            DefaultRequest(image: "nginx:latest", tagPatternFilter: "[unclosed"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.WebResourceId == svc.Id));
    }

    internal static DockerWatchUpsertRequest DefaultRequest(
        string label = "app",
        string image = "nginx:latest",
        string containerName = "svc",
        string? tagPatternFilter = null,
        CheckScheduleType scheduleType = CheckScheduleType.Hourly,
        int checkEveryHours = 24,
        TimeOnly? checkAtTime = null,
        DayOfWeek? checkOnDayOfWeek = null) =>
        new(
            Label: label,
            Enabled: true,
            ImageReference: image,
            ContainerName: containerName,
            RegistryUsername: null,
            RegistryPassword: null,
            ScheduleType: scheduleType,
            CheckEveryHours: checkEveryHours,
            CheckAtTime: checkAtTime,
            CheckOnDayOfWeek: checkOnDayOfWeek,
            TagPatternFilter: tagPatternFilter);
}



