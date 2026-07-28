using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Tests.Controllers.DockerWatches;

namespace Stashboard.Tests.Services;

/// <summary>
/// V9.2 — confirms <see cref="SelfUpdateReconciler"/> resolves Scheduled
/// self-update rows by comparing the container's actual digest to the target the
/// row recorded. Reuses the watch/connection seeding from
/// <see cref="DockerWatchesControllerTestBase"/>.
/// </summary>
public class SelfUpdateReconcilerTests : DockerWatchesControllerTestBase
{
    private const string PreviousDigest = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string TargetDigest = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";
    private const string OtherDigest = "sha256:cccc22222222222222222222222222222222222222222222222222222222cccc";

    private SelfUpdateReconciler BuildReconciler(Mock<IDockerHostClient> host) =>
        new(_dbContext, new DockerConnectionMapper(_encryptionMock.Object), host.Object,
            NullLogger<SelfUpdateReconciler>.Instance);

    private async Task<DockerWatchEntity> SeedSelfWatchAsync()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, label: "stashboard",
            imageReference: "vahac/stashboard:latest",
            registryHost: "docker.io", repository: "vahac/stashboard", tag: "latest",
            containerName: "stashboard-app",
            status: DockerUpdateStatus.UpdateAvailable);
        watch.CurrentDigest = PreviousDigest;
        watch.LatestDigest = TargetDigest;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return watch;
    }

    private async Task<DockerWatchEntity> SeedTrackedWatchAsync(string containerName, string latestDigest)
    {
        var svc = await _dataFactory.ServiceAsync(name: containerName);
        var watch = await SeedWatchAsync(svc.Id, _userId, label: containerName,
            imageReference: $"vahac/{containerName}:latest",
            registryHost: "docker.io", repository: $"vahac/{containerName}", tag: "latest",
            containerName: containerName,
            status: DockerUpdateStatus.UpdateAvailable);
        watch.CurrentDigest = PreviousDigest;
        watch.LatestDigest = latestDigest;
        _dbContext.DockerWatches.Update(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return watch;
    }

    private DockerUpdateAttemptEntity ScheduledChild(DockerWatchEntity watch, Guid parentId) => new()
    {
        Id = Guid.NewGuid(),
        ParentAttemptId = parentId,
        WebResourceId = watch.WebResourceId,
        DockerWatchId = watch.Id,
        DockerConnectionId = watch.DockerConnectionId,
        InitiatedByUserId = _userId,
        ActionType = DockerContainerActionType.Update,
        Status = DockerUpdateAttemptStatus.Scheduled,
        ImageReference = watch.ImageReference,
        ContainerName = watch.ContainerName,
        ComposeProject = "stashboard",
        PreviousDigest = PreviousDigest,
        NewDigest = watch.LatestDigest,
        CreatedUtc = DateTime.UtcNow,
        CompletedUtc = DateTime.UtcNow,
    };

    private async Task<DockerUpdateAttemptEntity> SeedScheduledAsync(DockerWatchEntity watch)
    {
        var attempt = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = watch.WebResourceId,
            DockerWatchId = watch.Id,
            DockerConnectionId = watch.DockerConnectionId,
            InitiatedByUserId = _userId,
            Status = DockerUpdateAttemptStatus.Scheduled,
            ImageReference = watch.ImageReference,
            ContainerName = watch.ContainerName,
            PreviousDigest = PreviousDigest,
            NewDigest = TargetDigest,
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerUpdateAttempts.Add(attempt);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return attempt;
    }

    private static Mock<IDockerHostClient> HostReturning(DockerHostResult result)
    {
        var host = new Mock<IDockerHostClient>();
        host.Setup(h => h.GetCurrentImageDigestAsync(
                It.IsAny<DockerHostTransport>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return host;
    }

    [Fact]
    public async Task Reconcile_ContainerReachedTarget_FlipsToSuccessAndClearsBadge()
    {
        var watch = await SeedSelfWatchAsync();
        var attempt = await SeedScheduledAsync(watch);
        var host = HostReturning(new DockerHostResult(DockerHostStatus.Ok, TargetDigest, null, null, null));

        await BuildReconciler(host).ReconcileAsync();

        var row = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        Assert.Equal(DockerUpdateAttemptStatus.Success, row.Status);

        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpToDate, watchRow!.UpdateStatus);
        Assert.Equal(TargetDigest, watchRow.CurrentDigest);
    }

    [Fact]
    public async Task Reconcile_ContainerOnDifferentImage_FlipsToFailedAndKeepsBadge()
    {
        var watch = await SeedSelfWatchAsync();
        var attempt = await SeedScheduledAsync(watch);
        // Container is still on a digest other than the target → recreate failed.
        var host = HostReturning(new DockerHostResult(DockerHostStatus.Ok, OtherDigest, null, null, null));

        await BuildReconciler(host).ReconcileAsync();

        var row = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, row.Status);
        Assert.NotNull(row.Error);

        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, watchRow!.UpdateStatus);
    }

    [Fact]
    public async Task Reconcile_HostUnreachable_LeavesScheduledForRetry()
    {
        var watch = await SeedSelfWatchAsync();
        var attempt = await SeedScheduledAsync(watch);
        var host = HostReturning(new DockerHostResult(DockerHostStatus.HostUnreachable, null, null, null, "dial unix: no such file"));

        await BuildReconciler(host).ReconcileAsync();

        var row = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id);
        Assert.Equal(DockerUpdateAttemptStatus.Scheduled, row.Status);
    }

    [Fact]
    public async Task Reconcile_MultiContainerProject_ReconcilesEachAndAggregatesParent()
    {
        // A project with two tracked containers: one reaches its target, one
        // doesn't. Each gets its own Scheduled child row + target digest, each is
        // reconciled independently, and the parent aggregates to RecreateFailed.
        var reached = await SeedTrackedWatchAsync("stashboard-app", TargetDigest);
        var stuck = await SeedTrackedWatchAsync("sidecar-app", TargetDigest);

        var parent = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = _userId,
            ActionType = DockerContainerActionType.UpdateProject,
            Status = DockerUpdateAttemptStatus.Scheduled,
            ImageReference = string.Empty,
            ContainerName = "compose:stashboard",
            ComposeProject = "stashboard",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        var childReached = ScheduledChild(reached, parent.Id);
        var childStuck = ScheduledChild(stuck, parent.Id);
        _dbContext.DockerUpdateAttempts.AddRange(parent, childReached, childStuck);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var host = new Mock<IDockerHostClient>();
        host.Setup(h => h.GetCurrentImageDigestAsync(
                It.IsAny<DockerHostTransport>(), "stashboard-app",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerHostResult(DockerHostStatus.Ok, TargetDigest, null, null, null));
        host.Setup(h => h.GetCurrentImageDigestAsync(
                It.IsAny<DockerHostTransport>(), "sidecar-app",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerHostResult(DockerHostStatus.Ok, OtherDigest, null, null, null));

        await BuildReconciler(host).ReconcileAsync();

        var rows = await _dbContext.DockerUpdateAttempts.AsNoTracking().ToListAsync();
        Assert.Equal(DockerUpdateAttemptStatus.Success, rows.Single(r => r.Id == childReached.Id).Status);
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, rows.Single(r => r.Id == childStuck.Id).Status);
        // Parent reflects "not everything reached its target".
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, rows.Single(r => r.Id == parent.Id).Status);

        Assert.Equal(DockerUpdateStatus.UpToDate, (await ReloadWatchAsync(reached.Id))!.UpdateStatus);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, (await ReloadWatchAsync(stuck.Id))!.UpdateStatus);
    }

    [Fact]
    public async Task Reconcile_MultiContainerProject_AllReachedTarget_ParentSuccess()
    {
        var a = await SeedTrackedWatchAsync("stashboard-app", TargetDigest);
        var b = await SeedTrackedWatchAsync("worker-app", TargetDigest);
        var parent = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = _userId,
            ActionType = DockerContainerActionType.UpdateProject,
            Status = DockerUpdateAttemptStatus.Scheduled,
            ImageReference = string.Empty,
            ContainerName = "compose:stashboard",
            ComposeProject = "stashboard",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerUpdateAttempts.AddRange(parent, ScheduledChild(a, parent.Id), ScheduledChild(b, parent.Id));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var host = HostReturning(new DockerHostResult(DockerHostStatus.Ok, TargetDigest, null, null, null));

        await BuildReconciler(host).ReconcileAsync();

        var parentRow = await _dbContext.DockerUpdateAttempts.AsNoTracking().SingleAsync(r => r.Id == parent.Id);
        Assert.Equal(DockerUpdateAttemptStatus.Success, parentRow.Status);
    }

    [Fact]
    public async Task Reconcile_NoScheduledRows_DoesNothing()
    {
        var watch = await SeedSelfWatchAsync();
        // A non-scheduled (Success) row must be left untouched and not inspected.
        var host = new Mock<IDockerHostClient>(MockBehavior.Strict);

        await BuildReconciler(host).ReconcileAsync();

        var watchRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, watchRow!.UpdateStatus);
        host.VerifyNoOtherCalls();
    }
}
