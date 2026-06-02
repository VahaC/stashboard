using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

/// <summary>
/// V5.5 — app-wide image-prune settings, surfaced on the Settings → Image
/// cleanup page.
/// </summary>
public sealed record ImagePruneSettingsResponse(
    /// <summary>Master switch for the background sweep.</summary>
    bool Enabled,
    /// <summary>How often the sweep runs per connection, in hours.</summary>
    int IntervalHours);

/// <summary>V5.5 — update payload for the image-prune settings.</summary>
public sealed record UpdateImagePruneSettingsRequest(
    bool Enabled,
    [Range(1, 8760)] int IntervalHours);

/// <summary>
/// V5.5 — payload for the storage widget on the V3.5 instances page.
/// Aggregates the host's image counts + sizes with the recent prune runs
/// for the connection so the widget can render
/// <em>"126 images, 4 dangling (4.2 GiB)"</em> and
/// <em>"Last prune: 2 days ago — freed 3.1 GiB"</em> in one round trip.
/// </summary>
public sealed record DockerImageStorageResponse(
    int TotalImageCount,
    int DanglingImageCount,
    long DanglingImageBytes,
    int UnusedImageCount,
    long UnusedImageBytes,
    /// <summary>Whether this connection participates in the background sweep
    /// (<c>DockerConnection.AllowImagePrune</c>).</summary>
    bool AllowImagePrune,
    /// <summary>Whether the connection has opted in to pruning unused
    /// (non-dangling) images on top of dangling ones.</summary>
    bool PruneUnusedImages,
    /// <summary>UTC of the last successful prune on this connection.</summary>
    DateTime? LastPruneUtc,
    /// <summary>Most recent prune-run audit rows for this connection,
    /// newest first.</summary>
    IReadOnlyList<DockerPruneRunResponse> RecentRuns,
    /// <summary>V5.5 — every image on the host (largest first), each flagged
    /// dangling / unused, so the widget's drill-down can list exactly which
    /// images make up each count.</summary>
    IReadOnlyList<DockerImageInfo> Images);

/// <summary>V5.5 — wire form of a single image for the Storage drill-down.</summary>
public sealed record DockerImageInfo(
    string Id,
    /// <summary>Repo tags (e.g. <c>nginx:1.27</c>); empty for a dangling image.</summary>
    IReadOnlyList<string> RepoTags,
    long SizeBytes,
    DateTime? CreatedUtc,
    bool IsDangling,
    bool IsUnused,
    /// <summary>Containers (running or stopped) using this image. Non-empty
    /// means a prune can't remove it until those containers are gone.</summary>
    IReadOnlyList<string> UsedByContainers);

/// <summary>
/// V5.5 — wire form of a single <c>DockerPruneRunEntity</c> row.
/// </summary>
public sealed record DockerPruneRunResponse(
    Guid Id,
    DockerPruneTrigger Trigger,
    DockerPruneStatus Status,
    bool IncludedUnused,
    int ImagesDeleted,
    long SpaceReclaimedBytes,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string? Error);

/// <summary>
/// V5.5 — body for <c>POST .../images/prune</c>. <see cref="IncludeUnused"/>
/// overrides the connection's <c>PruneUnusedImages</c> opt-in for a single
/// manual run so the user can ask for the aggressive sweep on demand.
/// </summary>
public sealed record PruneImagesRequest(bool IncludeUnused = false);

/// <summary>V5.5 — response from <c>POST .../images/prune</c>.</summary>
public sealed record PruneImagesResponse(DockerPruneRunResponse Run);
