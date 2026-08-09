using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class CheckNowEndpointTests : WebResourcesControllerTestBase
{
    private static ServiceCheckResult MainOnly(ServiceStatus status, int? ms = null, string? error = null)
        => new(new HealthCheckResult(status, ms, error), null);

    private static ServiceCheckResult WithAdditional(
        ServiceStatus mainStatus, int? mainMs,
        ServiceStatus addStatus, int? addMs, string? addError)
        => new(new HealthCheckResult(mainStatus, mainMs, null),
               new HealthCheckResult(addStatus, addMs, addError));

    [Fact]
    public async Task CheckNow_ReturnsOk_AndDatabaseRowHasUpdatedStatus()
    {
        var svc = await _dataFactory.ServiceAsync();
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MainOnly(ServiceStatus.Up, 120));
        var ctrl = BuildController();

        var result = await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal(ServiceStatus.Up, response.CurrentStatus);
        Assert.Equal(120, response.LastResponseTimeMs);
        Assert.Null(response.LastError);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Up, dbRow.CurrentStatus);
        Assert.Equal(120, dbRow.LastResponseTimeMs);
        Assert.Null(dbRow.LastError);
    }

    [Fact]
    public async Task CheckNow_DownStatus_DatabaseRowReflectsErrorAndNullResponseTime()
    {
        var svc = await _dataFactory.ServiceAsync();
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceCheckResult(new HealthCheckResult(ServiceStatus.Down, null, "Connection refused"), null));
        var ctrl = BuildController();

        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Down, dbRow.CurrentStatus);
        Assert.Equal("Connection refused", dbRow.LastError);
        Assert.Null(dbRow.LastResponseTimeMs);
    }

    [Fact]
    public async Task CheckNow_SetsLastCheckedUtc_InDatabase_ToApproximatelyNow()
    {
        var svc = await _dataFactory.ServiceAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MainOnly(ServiceStatus.Up, 50));
        var ctrl = BuildController();

        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.LastCheckedUtc);
        Assert.True(dbRow.LastCheckedUtc >= before);
        Assert.True(dbRow.LastCheckedUtc <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task CheckNow_OverwritesPreviousStatus_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceCheckResult(new HealthCheckResult(ServiceStatus.Down, null, "timeout"), null));
        var ctrl = BuildController();
        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MainOnly(ServiceStatus.Up, 80));
        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Up, dbRow.CurrentStatus);
        Assert.Equal(80, dbRow.LastResponseTimeMs);
        Assert.Null(dbRow.LastError);
    }

    [Fact]
    public async Task CheckNow_CallsHealthChecker_WithTheCorrectServiceFromDatabase()
    {
        var svc = await _dataFactory.ServiceAsync(mainUrl: "https://target.example.com");
        Core.Entities.WebResourceEntity? captured = null;
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .Callback<Core.Entities.WebResourceEntity, Stashboard.Core.Abstractions.HealthCheckRetrySettings?, CancellationToken>((s, _, __) => captured = s)
            .ReturnsAsync(MainOnly(ServiceStatus.Up, 100));
        var ctrl = BuildController();

        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(svc.Id, captured!.Id);
        Assert.Equal("https://target.example.com", captured.MainUrl);
    }

    [Fact]
    public async Task CheckNow_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var nonExistentId = Guid.NewGuid();
        var ctrl = BuildController();

        var result = await ctrl.CheckNow(nonExistentId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == nonExistentId));
    }

    [Fact]
    public async Task CheckNow_ReturnsNotFound_AndDoesNotChangeOtherUserRow()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var originalStatus = svc.CurrentStatus;
        var ctrl = BuildController();

        var result = await ctrl.CheckNow(svc.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(originalStatus, dbRow.CurrentStatus);
        Assert.Null(dbRow.LastCheckedUtc);
    }

    [Fact]
    public async Task CheckNow_DoesNotInvokeHealthChecker_WhenServiceNotFound()
    {
        var ctrl = BuildController();

        await ctrl.CheckNow(Guid.NewGuid(), CancellationToken.None);

        _healthCheckerMock.Verify(
            h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckNow_WithAdditionalUrl_PersistsBothStatuses_MainUpAdditionalDown()
    {
        var svc = await _dataFactory.ServiceAsync(mainUrl: "https://main.example.com");
        await _dbContext.WebResources
            .Where(s => s.Id == svc.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AdditionalUrl, "https://additional.example.com"));
        _dbContext.ChangeTracker.Clear();

        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WithAdditional(ServiceStatus.Up, 100, ServiceStatus.Down, 200, "HTTP 503"));
        var ctrl = BuildController();

        var result = await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal(ServiceStatus.Up, response.CurrentStatus);
        Assert.Equal(ServiceStatus.Down, response.AdditionalUrlStatus);
        Assert.Equal("HTTP 503", response.AdditionalUrlLastError);
        Assert.Equal(200, response.AdditionalUrlLastResponseTimeMs);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Up, dbRow.CurrentStatus);
        Assert.Equal(ServiceStatus.Down, dbRow.AdditionalUrlStatus);
        Assert.Equal("HTTP 503", dbRow.AdditionalUrlLastError);
        Assert.Equal(200, dbRow.AdditionalUrlLastResponseTimeMs);
    }

    [Fact]
    public async Task CheckNow_WithoutAdditionalUrl_AdditionalStatusRemainsUnknown()
    {
        var svc = await _dataFactory.ServiceAsync();
        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MainOnly(ServiceStatus.Up, 50));
        var ctrl = BuildController();

        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Unknown, dbRow.AdditionalUrlStatus);
        Assert.Null(dbRow.AdditionalUrlLastError);
    }

    [Fact]
    public async Task CheckNow_WithAdditionalUrl_BothUp_PersistsBothStatusesUp()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dbContext.WebResources
            .Where(s => s.Id == svc.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AdditionalUrl, "https://mirror.example.com"));
        _dbContext.ChangeTracker.Clear();

        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<Core.Entities.WebResourceEntity>(), It.IsAny<Stashboard.Core.Abstractions.HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WithAdditional(ServiceStatus.Up, 80, ServiceStatus.Up, 90, null));
        var ctrl = BuildController();

        await ctrl.CheckNow(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(ServiceStatus.Up, dbRow.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, dbRow.AdditionalUrlStatus);
        Assert.Equal(90, dbRow.AdditionalUrlLastResponseTimeMs);
        Assert.Null(dbRow.AdditionalUrlLastError);
    }
}



