using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DockerWatchStatusWriter.ApplyCheckResult"/> — the
/// shared write-back used by both the background scan and the manual "Check
/// now" endpoints. Focuses on the latest-pointer + release-notes rules.
/// </summary>
public class DockerWatchStatusWriterTests
{
    private const string DigestA = "sha256:aaaa";
    private const string DigestB = "sha256:bbbb";

    private static DockerWatchEntity WatchWithDetectedUpdate() =>
        new()
        {
            UpdateStatus = DockerUpdateStatus.UpdateAvailable,
            CurrentDigest = DigestA,
            LatestDigest = DigestB,
            CurrentVersionTag = "latest",
            LatestVersionTag = "v2.0.0",
            LatestReleaseUrl = "https://example/releases/v2.0.0",
            LatestReleaseBody = "notes",
        };

    private static DockerCheckResult Result(
        DockerUpdateStatus status,
        string? currentDigest,
        string? latestDigest,
        string? latestTag,
        string? error) =>
        new(status, currentDigest, latestDigest, "latest", latestTag, error, DateTime.UtcNow);

    [Fact]
    public void ZeroMatchUpToDate_ClearsStaleLatestPointerAndReleaseNotes()
    {
        var watch = WatchWithDetectedUpdate();

        // Filter now matches zero tags → UpToDate with null latest + explanation.
        DockerWatchStatusWriter.ApplyCheckResult(watch,
            Result(DockerUpdateStatus.UpToDate, DigestA, null, null, "No tags matched the filter 'x'."));

        Assert.Equal(DockerUpdateStatus.UpToDate, watch.UpdateStatus);
        Assert.Null(watch.LatestDigest);
        Assert.Null(watch.LatestVersionTag);
        Assert.Null(watch.LatestReleaseUrl);
        Assert.Null(watch.LatestReleaseBody);
        Assert.Equal(DigestA, watch.CurrentDigest);
    }

    [Fact]
    public void TransientError_PreservesLastKnownGoodLatestPointerAndReleaseNotes()
    {
        var watch = WatchWithDetectedUpdate();

        // Registry rate-limited → Error with null latest. Must NOT wipe the
        // previously-detected update.
        DockerWatchStatusWriter.ApplyCheckResult(watch,
            Result(DockerUpdateStatus.Error, DigestA, null, null, "Registry rate-limited the request."));

        Assert.Equal(DockerUpdateStatus.Error, watch.UpdateStatus);
        Assert.Equal(DigestB, watch.LatestDigest);
        Assert.Equal("v2.0.0", watch.LatestVersionTag);
        Assert.Equal("https://example/releases/v2.0.0", watch.LatestReleaseUrl);
        Assert.Equal("notes", watch.LatestReleaseBody);
    }

    [Fact]
    public void UpdateAvailable_WithNewDigest_StampsDetectedAtAndRefreshesNotes()
    {
        var watch = WatchWithDetectedUpdate();
        watch.LatestDigest = DigestA; // pretend previous latest differs from new

        var result = new DockerCheckResult(
            DockerUpdateStatus.UpdateAvailable, DigestA, DigestB, "latest", "v3.0.0", null,
            DateTime.UtcNow, "https://example/releases/v3.0.0", "v3 notes");

        DockerWatchStatusWriter.ApplyCheckResult(watch, result);

        Assert.Equal(DigestB, watch.LatestDigest);
        Assert.Equal("v3.0.0", watch.LatestVersionTag);
        Assert.Equal("https://example/releases/v3.0.0", watch.LatestReleaseUrl);
        Assert.Equal(result.CheckedAtUtc, watch.LastUpdateDetectedUtc);
    }
}



