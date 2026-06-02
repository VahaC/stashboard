using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V5.5 — runs <c>docker image prune</c> for a single Docker connection and
/// returns a result-style envelope. Persistence of the audit row is the
/// caller's job — keeping the runner free of <c>ApplicationDbContext</c>
/// lets the controller and the background service share one orchestrator
/// without each owning a scope.
/// </summary>
public sealed class DockerPruneRunner(
    IDockerHostClient hostClient,
    TimeProvider timeProvider,
    ILogger<DockerPruneRunner> logger) : IDockerPruneRunner
{
    public async Task<DockerPruneRunResult> RunAsync(
        DockerPruneRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedUtc = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var prune = await hostClient.PruneImagesAsync(request.Transport, request.IncludeUnused, cancellationToken);
            var completedUtc = timeProvider.GetUtcNow().UtcDateTime;

            if (!prune.IsSuccess)
            {
                logger.LogWarning("Image prune failed for connection {ConnectionId}: {Error}",
                    request.ConnectionId, prune.Error);
                var status = prune.Status == DockerHostStatus.HostUnreachable
                    ? DockerPruneStatus.HostUnreachable
                    : DockerPruneStatus.Failed;
                return new DockerPruneRunResult(
                    status, 0, 0, startedUtc, completedUtc, request.IncludeUnused, prune.Error);
            }

            var outcome = prune.ImagesDeleted == 0 && prune.SpaceReclaimedBytes == 0
                ? DockerPruneStatus.NothingToPrune
                : DockerPruneStatus.Success;
            return new DockerPruneRunResult(
                outcome,
                prune.ImagesDeleted,
                prune.SpaceReclaimedBytes,
                startedUtc,
                completedUtc,
                request.IncludeUnused,
                Error: null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image prune crashed for connection {ConnectionId}", request.ConnectionId);
            return new DockerPruneRunResult(
                DockerPruneStatus.Failed, 0, 0, startedUtc,
                timeProvider.GetUtcNow().UtcDateTime, request.IncludeUnused, ex.Message);
        }
    }
}
