using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// V9.2 — confirms self-updates that were handed off to the detached helper. A
/// self-update recreates the Stashboard container out of band, so the request
/// that scheduled it can't observe the outcome — it only writes a
/// <see cref="DockerUpdateAttemptStatus.Scheduled"/> attempt row carrying the
/// digest it was trying to reach (the watch's <c>LatestDigest</c>). This service
/// reads the container's <em>actual</em> current digest and compares it to that
/// target: a match flips the row to <c>Success</c> and clears the watch's
/// "Update available" badge; a mismatch flips it to <c>RecreateFailed</c> and
/// leaves the badge in place. Covers both single ("Update now") rows and
/// per-service project ("Update project") rows; a project parent row is resolved
/// from its children.
/// </summary>
/// <remarks>
/// The digest is read straight off the running container (registry-independent),
/// so confirmation works even if the registry is unreachable. When the current
/// digest can't be read at all (host down), the row is left <c>Scheduled</c> so a
/// later run retries — an outcome is never guessed.
/// </remarks>
public sealed class SelfUpdateReconciler(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IDockerHostClient hostClient,
    ILogger<SelfUpdateReconciler> logger)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var scheduled = await db.DockerUpdateAttempts
            .Where(a => a.Status == DockerUpdateAttemptStatus.Scheduled)
            .ToListAsync(cancellationToken);
        if (scheduled.Count == 0)
            return;

        logger.LogInformation("Self-update reconcile: {Count} scheduled attempt(s) to confirm.", scheduled.Count);
        var now = DateTime.UtcNow;

        // Leaf rows (a single self-update, or a project's per-service row): each
        // carries the watch and the digest we expected to be running.
        foreach (var attempt in scheduled.Where(a => a.DockerWatchId is not null))
            await ReconcileLeafAsync(attempt, now, cancellationToken);

        // Project parent rows: resolve from their (now reconciled) children.
        foreach (var parent in scheduled.Where(a => a.DockerWatchId is null && a.ComposeProject is not null))
        {
            var children = scheduled.Where(c => c.ParentAttemptId == parent.Id).ToList();
            if (children.Count == 0)
                continue; // nothing confirmable — leave it Scheduled
            parent.Status = children.All(c => c.Status == DockerUpdateAttemptStatus.Success)
                ? DockerUpdateAttemptStatus.Success
                : DockerUpdateAttemptStatus.RecreateFailed;
            parent.CompletedUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileLeafAsync(DockerUpdateAttemptEntity attempt, DateTime now, CancellationToken cancellationToken)
    {
        if (attempt.NewDigest is null)
            return; // no target digest recorded — can't confirm by digest; leave Scheduled

        var watch = await db.DockerWatches.FirstOrDefaultAsync(w => w.Id == attempt.DockerWatchId, cancellationToken);
        if (watch is null)
        {
            Fail(attempt, now, "Self-update could not be confirmed: the watch no longer exists.");
            return;
        }

        var connection = await db.DockerConnections.FirstOrDefaultAsync(c => c.Id == watch.DockerConnectionId, cancellationToken);
        if (connection is null)
        {
            Fail(attempt, now, "Self-update could not be confirmed: the Docker connection no longer exists.");
            return;
        }

        var transport = connectionMapper.BuildTransport(connection);
        var current = await hostClient.GetCurrentImageDigestAsync(
            transport, watch.ContainerName, watch.RegistryHost, watch.Repository, cancellationToken);

        if (!current.IsSuccess)
        {
            // Couldn't read the running digest (host unreachable, etc.). Never
            // guess — leave the row Scheduled so a later run retries.
            logger.LogInformation(
                "Self-update reconcile: couldn't read current digest for {Container} ({Error}); leaving scheduled to retry.",
                watch.ContainerName, current.Error);
            return;
        }

        attempt.CompletedUtc = now;
        if (string.Equals(current.Digest, attempt.NewDigest, StringComparison.OrdinalIgnoreCase))
        {
            attempt.Status = DockerUpdateAttemptStatus.Success;
            watch.CurrentDigest = attempt.NewDigest;
            watch.UpdateStatus = DockerUpdateStatus.UpToDate;
            watch.LastError = null;
            watch.UpdatedUtc = now;
            logger.LogInformation("Self-update reconcile: {Container} reached the target image → success.", watch.ContainerName);
        }
        else
        {
            attempt.Status = DockerUpdateAttemptStatus.RecreateFailed;
            attempt.Error = "Self-update did not take effect: the container is running a different image than the target.";
            // The watch keeps CurrentDigest != LatestDigest, so it stays "Update
            // available"; surface the failure on the watch too.
            watch.LastError = "Self-update did not complete — the container is not running the new image.";
            watch.UpdatedUtc = now;
            logger.LogWarning("Self-update reconcile: {Container} did NOT reach the target image → failed.", watch.ContainerName);
        }
    }

    private static void Fail(DockerUpdateAttemptEntity attempt, DateTime now, string error)
    {
        attempt.Status = DockerUpdateAttemptStatus.RecreateFailed;
        attempt.Error = error;
        attempt.CompletedUtc = now;
    }
}
