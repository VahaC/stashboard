namespace Stashboard.Api.Contracts;

// ── Management (authenticated, owner-scoped) ────────────────────────────────

/// <summary>V10.2 — one selected service inside a status-page management payload.</summary>
public sealed record StatusPageItemResponse(
    Guid WebResourceId,
    string ServiceName,
    string? DisplayName,
    int SortOrder);

/// <summary>V10.2 — a status page as seen by its owner in the management UI.</summary>
public sealed record StatusPageResponse(
    Guid Id,
    string Title,
    string? Description,
    string Slug,
    bool IsPublished,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<StatusPageItemResponse> Items);

/// <summary>V10.2 — create / update a status page. <see cref="Slug"/> is optional on create
/// (derived from the title when blank). Items are the full desired selection (replace semantics).</summary>
public sealed record StatusPageUpsertRequest(
    string Title,
    string? Description,
    string? Slug,
    bool IsPublished,
    List<StatusPageItemUpsert> Items);

public sealed record StatusPageItemUpsert(
    Guid WebResourceId,
    string? DisplayName);

// ── Public (anonymous, whitelisted display fields only) ─────────────────────

/// <summary>V10.2 — one day of the public recent-history bar. <see cref="Uptime"/> is a
/// percentage (0–100) or null when the service wasn't monitored that day.</summary>
public sealed record PublicStatusHistoryBucket(
    DateTime DateUtc,
    double? Uptime,
    string Status);

/// <summary>
/// V10.2 — a single service on the public status page. Display-only: the public display name,
/// the live status, rolled-up uptime %s and the recent-history bar. Deliberately carries no id,
/// URL, notes, category, tags or any other owner-private field.
/// </summary>
public sealed record PublicStatusServiceResponse(
    string Name,
    string Status,
    double? Uptime24h,
    double? Uptime7d,
    double? Uptime30d,
    IReadOnlyList<PublicStatusHistoryBucket> History);

/// <summary>
/// V10.2 — the public status-page payload returned by <c>GET /api/status/{slug}</c> for a
/// published page. <see cref="OverallStatus"/> is the aggregate banner state
/// (operational / degraded / down / unknown).
/// </summary>
public sealed record PublicStatusPageResponse(
    string Title,
    string? Description,
    DateTime GeneratedUtc,
    string OverallStatus,
    IReadOnlyList<PublicStatusServiceResponse> Services);
