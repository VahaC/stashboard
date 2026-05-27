using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// Shared write-back of a <see cref="DockerCheckResult"/> onto a
/// <see cref="DockerWatchEntity"/>. Extracted so the manual "Check now"
/// endpoints (service- and connection-scoped) and the background scan apply
/// the exact same digest / release-notes / status rules. The caller owns the
/// <c>SaveChangesAsync</c>.
/// </summary>
public static class DockerWatchStatusWriter
{
    public static void ApplyCheckResult(DockerWatchEntity watch, DockerCheckResult result)
    {
        var previousLatest = watch.LatestDigest;
        watch.UpdateStatus = result.Status;
        watch.CurrentDigest = result.CurrentDigest ?? watch.CurrentDigest;
        watch.CurrentVersionTag = result.CurrentVersionTag ?? watch.CurrentVersionTag;

        if (result.Status == DockerUpdateStatus.Error)
        {
            // Transient failure (host down, registry rate-limited, …): keep the
            // last-known-good latest pointer so a flaky tick doesn't wipe a
            // previously-detected update from the UI.
            watch.LatestDigest = result.LatestDigest ?? watch.LatestDigest;
            watch.LatestVersionTag = result.LatestVersionTag ?? watch.LatestVersionTag;
        }
        else
        {
            // Definitive comparison (UpToDate / UpdateAvailable): trust the
            // result verbatim — including nulls. This clears a stale "latest
            // vX" pointer when a tag-pattern filter now matches zero tags
            // instead of leaving a phantom candidate behind.
            watch.LatestDigest = result.LatestDigest;
            watch.LatestVersionTag = result.LatestVersionTag;
        }

        watch.LastCheckedUtc = result.CheckedAtUtc;
        watch.LastError = result.Error;
        watch.UpdatedUtc = result.CheckedAtUtc;

        // Release-notes enrichment: refresh the persisted pair on every digest
        // change; otherwise only fill in additional info, never blank out a
        // previously-known value (enrichment is best-effort per tick). Keyed on
        // the digest we actually wrote so the error path (digest preserved)
        // doesn't drop the existing notes.
        var digestChanged = !string.Equals(previousLatest, watch.LatestDigest, StringComparison.OrdinalIgnoreCase);
        if (digestChanged)
        {
            watch.LatestReleaseUrl = result.LatestReleaseUrl;
            watch.LatestReleaseBody = result.LatestReleaseBody;
        }
        else if (result.LatestReleaseUrl is not null || result.LatestReleaseBody is not null)
        {
            watch.LatestReleaseUrl = result.LatestReleaseUrl ?? watch.LatestReleaseUrl;
            watch.LatestReleaseBody = result.LatestReleaseBody ?? watch.LatestReleaseBody;
        }

        if (result.Status == DockerUpdateStatus.UpdateAvailable && digestChanged)
        {
            watch.LastUpdateDetectedUtc = result.CheckedAtUtc;
        }
    }
}
