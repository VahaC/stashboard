using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V2.7 — `POST /watches/{id}/update` ("Update now"). Covers the happy
/// path (pull + recreate + re-check flips status to UpToDate),
/// every failure-mode mapped from the updater, the per-attempt audit row
/// persistence, the owner-only access check, and the rule that disabled
/// watches refuse to update.
/// </summary>
public class UpdateNowEndpointTests : DockerWatchesControllerTestBase
{
    private const string PreviousDigest = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string NewDigest = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";

    [Fact]
    public async Task Update_SuccessfulPullRecreate_PersistsAttemptAndFlipsWatchToUpToDate()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app",
            imageReference: "ghcr.io/owner/repo:v1",
            registryHost: "ghcr.io", repository: "owner/repo", tag: "v1",
            containerName: "wp",
            status: DockerUpdateStatus.UpdateAvailable);
        // Seed the previously-observed update digest so the orchestrator
        // re-check has something concrete to compare against.
        watch.CurrentDigest = PreviousDigest;
        watch.LatestDigest = NewDigest;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _imageUpdaterMock
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(
                DockerUpdateAttemptStatus.Success, PreviousDigest, NewDigest, null));

        // After a successful recreate the controller re-checks; the new
        // digest should now match what the registry reports, flipping the
        // watch to UpToDate.
        var checkedAt = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        _updateCheckerMock
            .Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpToDate, NewDigest, NewDigest, "v1", "v1", null, checkedAt));

        var ctrl = BuildController();
        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchUpdateResponse>(ok.Value);
        Assert.Equal(DockerUpdateAttemptStatus.Success, response.Attempt.Status);
        Assert.Equal(PreviousDigest, response.Attempt.PreviousDigest);
        Assert.Equal(NewDigest, response.Attempt.NewDigest);
        Assert.Null(response.Attempt.Error);
        Assert.Equal(DockerUpdateStatus.UpToDate, response.Watch.UpdateStatus);
        Assert.Equal(NewDigest, response.Watch.CurrentDigest);

        // Audit row persisted.
        var attemptRow = await _dbContext.DockerUpdateAttempts.AsNoTracking()
            .SingleAsync(a => a.DockerWatchId == watch.Id);
        Assert.Equal(DockerUpdateAttemptStatus.Success, attemptRow.Status);
        Assert.Equal(watch.ImageReference, attemptRow.ImageReference);
        Assert.Equal(watch.ContainerName, attemptRow.ContainerName);
        Assert.Equal(_userId, attemptRow.InitiatedByUserId);
        Assert.Equal(PreviousDigest, attemptRow.PreviousDigest);
        Assert.Equal(NewDigest, attemptRow.NewDigest);

        // Watch row updated by the post-update re-check.
        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpToDate, watchRow!.UpdateStatus);
        Assert.Equal(NewDigest, watchRow.CurrentDigest);
    }

    [Fact]
    public async Task Update_PullFailure_PersistsAttempt_AndDoesNotReCheck()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app",
            status: DockerUpdateStatus.UpdateAvailable);
        _imageUpdaterMock
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(
                DockerUpdateAttemptStatus.PullFailed, PreviousDigest, null,
                "docker pull failed: rate limit"));

        var ctrl = BuildController();
        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchUpdateResponse>(ok.Value);
        Assert.Equal(DockerUpdateAttemptStatus.PullFailed, response.Attempt.Status);
        Assert.Contains("rate limit", response.Attempt.Error);

        var attemptRow = await _dbContext.DockerUpdateAttempts.AsNoTracking()
            .SingleAsync(a => a.DockerWatchId == watch.Id);
        Assert.Equal(DockerUpdateAttemptStatus.PullFailed, attemptRow.Status);
        Assert.Null(attemptRow.NewDigest);

        // The post-update re-check must not run on a failed pull — the
        // original container's state hasn't changed, so re-querying the
        // registry would just burn rate-limit quota.
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_HostUnreachable_PersistsAttempt_WatchUnchanged()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");
        watch.UpdateStatus = DockerUpdateStatus.UpdateAvailable;
        watch.CurrentDigest = PreviousDigest;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _imageUpdaterMock
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(
                DockerUpdateAttemptStatus.HostUnreachable, null, null,
                "Docker host unreachable: dial unix /var/run/docker.sock: no such file or directory"));

        var ctrl = BuildController();
        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchUpdateResponse>(ok.Value);
        Assert.Equal(DockerUpdateAttemptStatus.HostUnreachable, response.Attempt.Status);

        // The watch's status / digests stay untouched because we never got
        // to recreate anything.
        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, watchRow!.UpdateStatus);
        Assert.Equal(PreviousDigest, watchRow.CurrentDigest);
    }

    [Fact]
    public async Task Update_DisabledWatch_ReturnsBadRequest_NoUpdaterCall()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app", enabled: false);
        var ctrl = BuildController();

        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _imageUpdaterMock.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await _dbContext.DockerUpdateAttempts.AnyAsync(a => a.DockerWatchId == watch.Id));
    }

    [Fact]
    public async Task Update_WatchMissing_ReturnsNotFound()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UpdateNow(svc.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _imageUpdaterMock.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ForeignWatch_ReturnsNotFound_NoSideEffects()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController(); // current user, not the owner

        var result = await ctrl.UpdateNow(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _imageUpdaterMock.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await _dbContext.DockerUpdateAttempts.AnyAsync());
    }

    [Fact]
    public async Task Update_SelfTarget_SchedulesDetachedHelper_AndSkipsInProcessRecreate()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "stashboard",
            imageReference: "vahac/stashboard:latest",
            registryHost: "docker.io", repository: "vahac/stashboard", tag: "latest",
            containerName: "stashboard-app",
            status: DockerUpdateStatus.UpdateAvailable);
        // The target the self-update is trying to reach.
        watch.CurrentDigest = PreviousDigest;
        watch.LatestDigest = NewDigest;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _selfUpdateLauncherMock
            .Setup(l => l.IsSelfTargetAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _selfUpdateLauncherMock
            .Setup(l => l.LaunchAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfUpdateLaunchResult(true, null));

        var ctrl = BuildController();
        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchUpdateResponse>(ok.Value);
        Assert.Equal(DockerUpdateAttemptStatus.Scheduled, response.Attempt.Status);

        // The badge is NOT cleared yet — the recreate runs out of band and is only
        // confirmed (or failed) by the startup reconciler comparing digests.
        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, watchRow!.UpdateStatus);

        // The in-process recreate must NOT run — that's the very bug we avoid.
        _imageUpdaterMock.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
        // No synchronous re-check for a scheduled self-update.
        _updateCheckerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);

        // The Scheduled row records the digest we're trying to reach so the
        // reconciler can confirm it on the next startup.
        var attemptRow = await _dbContext.DockerUpdateAttempts.AsNoTracking()
            .SingleAsync(a => a.DockerWatchId == watch.Id);
        Assert.Equal(DockerUpdateAttemptStatus.Scheduled, attemptRow.Status);
        Assert.Equal(NewDigest, attemptRow.NewDigest);
    }

    [Fact]
    public async Task Update_SelfTarget_HelperFailsToStart_PersistsRecreateFailed()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "stashboard",
            containerName: "stashboard-app",
            status: DockerUpdateStatus.UpdateAvailable);

        _selfUpdateLauncherMock
            .Setup(l => l.IsSelfTargetAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _selfUpdateLauncherMock
            .Setup(l => l.LaunchAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelfUpdateLaunchResult(false, "Docker socket is read-only."));

        var ctrl = BuildController();
        var result = await ctrl.UpdateNow(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DockerWatchUpdateResponse>(ok.Value);
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, response.Attempt.Status);
        Assert.Contains("read-only", response.Attempt.Error);

        _imageUpdaterMock.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListUpdates_ReturnsAttemptsNewestFirst()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "app");

        var older = new Core.Entities.DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = svc.Id,
            DockerWatchId = watch.Id,
            InitiatedByUserId = _userId,
            Status = DockerUpdateAttemptStatus.Success,
            ImageReference = watch.ImageReference,
            ContainerName = watch.ContainerName,
            PreviousDigest = PreviousDigest,
            NewDigest = NewDigest,
            CompletedUtc = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            CreatedUtc = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Core.Entities.DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = svc.Id,
            DockerWatchId = watch.Id,
            InitiatedByUserId = _userId,
            Status = DockerUpdateAttemptStatus.PullFailed,
            ImageReference = watch.ImageReference,
            ContainerName = watch.ContainerName,
            Error = "rate limit",
            CompletedUtc = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            CreatedUtc = new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
        };
        _dbContext.DockerUpdateAttempts.AddRange(older, newer);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var result = await ctrl.ListUpdates(svc.Id, watch.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rows = Assert.IsType<List<DockerUpdateAttemptResponse>>(ok.Value);
        Assert.Equal(2, rows.Count);
        // Newest first.
        Assert.Equal(newer.Id, rows[0].Id);
        Assert.Equal(older.Id, rows[1].Id);
    }

    [Fact]
    public async Task ListUpdates_ForeignWatch_ReturnsNotFound()
    {
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var foreignWatch = await SeedWatchAsync(foreignSvc.Id, _otherUserId, label: "app");
        var ctrl = BuildController();

        var result = await ctrl.ListUpdates(foreignSvc.Id, foreignWatch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListUpdates_WatchOnDifferentService_ReturnsNotFound()
    {
        var svc1 = await _dataFactory.ServiceAsync(name: "A");
        var svc2 = await _dataFactory.ServiceAsync(name: "B");
        var watch = await SeedWatchAsync(svc1.Id, _userId, label: "app");
        var ctrl = BuildController();

        // The watch belongs to svc1 — looking it up via svc2 must 404 even
        // though both services belong to the same user. Prevents path
        // tampering from leaking history rows across services.
        var result = await ctrl.ListUpdates(svc2.Id, watch.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
