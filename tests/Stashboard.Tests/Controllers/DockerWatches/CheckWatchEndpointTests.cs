using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

public class CheckWatchEndpointTests : DockerWatchesControllerTestBase
{
    private const string DigestCurrent = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string DigestLatest = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";

    [Fact]
    public async Task Check_PersistsUpToDateResultOntoEntity()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var checkedAt = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpToDate, DigestCurrent, DigestCurrent, "v1", "v1", null, checkedAt));

        var ctrl = BuildController();

        var result = await ctrl.Check(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(DockerUpdateStatus.UpToDate, response.UpdateStatus);
        Assert.Equal(DigestCurrent, response.CurrentDigest);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(DockerUpdateStatus.UpToDate, dbRow!.UpdateStatus);
        Assert.Equal(DigestCurrent, dbRow.CurrentDigest);
        Assert.Equal(checkedAt, dbRow.LastCheckedUtc);
    }

    [Fact]
    public async Task Check_StampsLastUpdateDetectedUtc_OnFirstUpdateAvailable()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var checkedAt = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpdateAvailable, DigestCurrent, DigestLatest, "v1", "v2", null, checkedAt));

        var ctrl = BuildController();
        await ctrl.Check(svc.Id, watch.Id, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, dbRow!.UpdateStatus);
        Assert.Equal(DigestLatest, dbRow.LatestDigest);
        Assert.Equal(checkedAt, dbRow.LastUpdateDetectedUtc);
    }

    [Fact]
    public async Task Check_DoesNotReStampLastUpdateDetected_WhenSameLatestDigestSeenAgain()
    {
        var svc = await _dataFactory.ServiceAsync();
        var firstDetected = new DateTime(2026, 5, 14, 0, 0, 0, DateTimeKind.Utc);
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        watch.LatestDigest = DigestLatest;
        watch.LastUpdateDetectedUtc = firstDetected;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var laterCheck = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpdateAvailable, DigestCurrent, DigestLatest, "v1", "v2", null, laterCheck));

        var ctrl = BuildController();
        await ctrl.Check(svc.Id, watch.Id, CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(firstDetected, dbRow!.LastUpdateDetectedUtc);
    }

    [Fact]
    public async Task Check_PersistsErrorStatusAndMessage()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        var checkedAt = DateTime.UtcNow;
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.Error, null, null, "v1", null, "registry unreachable", checkedAt));

        var ctrl = BuildController();
        var result = await ctrl.Check(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(DockerUpdateStatus.Error, response.UpdateStatus);
        Assert.Equal("registry unreachable", response.LastError);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal("registry unreachable", dbRow!.LastError);
    }

    [Fact]
    public async Task Check_DisabledWatch_SkipsOrchestratorAndMarksDisabled()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", enabled: false);
        var ctrl = BuildController();

        var result = await ctrl.Check(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Equal(DockerUpdateStatus.Disabled, response.UpdateStatus);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(DockerUpdateStatus.Disabled, dbRow!.UpdateStatus);

        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Check_OnlyChecksTheTargetedWatch_NotSiblings()
    {
        var svc = await _dataFactory.ServiceAsync();
        var appWatch = await SeedWatchAsync(svc.Id, _userId, label: "app", containerName: "wp-app");
        var dbWatch = await SeedWatchAsync(svc.Id, _userId, label: "db", containerName: "wp-db");
        var checkedAt = DateTime.UtcNow;
        DockerWatchProfile? captured = null;
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .Callback<DockerWatchProfile, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpToDate, DigestCurrent, DigestCurrent, "v1", "v1", null, checkedAt));

        var ctrl = BuildController();
        await ctrl.Check(svc.Id, dbWatch.Id, CancellationToken.None);

        // Only the targeted watch's container goes through the orchestrator.
        Assert.NotNull(captured);
        Assert.Equal("wp-db", captured!.ContainerName);
        // Sibling stays untouched.
        var siblingRow = await ReloadWatchAsync(appWatch.Id);
        Assert.NotNull(siblingRow);
        Assert.Null(siblingRow!.LastCheckedUtc);
    }

    [Fact]
    public async Task Check_ReturnsNotFound_WhenWatchMissing()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.Check(svc.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Check_ReturnsNotFound_WhenServiceBelongsToAnotherUser()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.Check(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
