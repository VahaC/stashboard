using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

/// <summary>
/// Tri-state action for sensitive fields in upsert and test-connection
/// requests, so an edit can preserve an already-encrypted value on the
/// entity without sending it round-trip.
/// </summary>
public enum SecretValueAction
{
    /// <summary>Leave the persisted (or absent) value unchanged.</summary>
    Keep = 0,
    /// <summary>Replace with the supplied <c>Value</c>.</summary>
    Set = 1,
    /// <summary>Drop the persisted value (set encrypted column to NULL).</summary>
    Clear = 2,
}

/// <summary>Wire format for a tri-state secret field.</summary>
public sealed record SecretValueUpsert(SecretValueAction Action, string? Value);

/// <summary>
/// Read model for a Docker watch. Host configuration is carried separately on
/// the service's <see cref="DockerConnectionResponse"/> — a watch only describes
/// the container it tracks. Never returns decrypted registry credentials — only
/// a presence flag.
/// </summary>
/// <remarks>
/// V2.2 replaces the V1 <c>CheckIntervalMinutes</c> field with a
/// <see cref="CheckScheduleType"/> plus the schedule parameters that are
/// relevant for the chosen mode (<see cref="CheckEveryHours"/> for Hourly,
/// <see cref="CheckAtTime"/> + <see cref="CheckOnDayOfWeek"/> for Daily / Weekly).
/// </remarks>
public sealed record DockerWatchResponse(
    Guid Id,
    /// <summary>V3.6 — the Docker host connection this container runs on.
    /// Always populated.</summary>
    Guid DockerConnectionId,
    /// <summary>V3.6 — optional link to a service. <c>null</c> for a standalone
    /// tracked container.</summary>
    Guid? WebResourceId,
    string Label,
    bool Enabled,
    string ImageReference,
    string RegistryHost,
    string Repository,
    string Tag,
    string ContainerName,
    bool HasRegistryCredentials,
    /// <summary>V2.3 — true when the watch stores an encrypted GitHub PAT
    /// for release-notes enrichment of private GHCR images. The PAT itself
    /// is never returned.</summary>
    bool HasGitHubPat,
    /// <summary>V2.4 — chosen authentication strategy. <c>Auto</c> for the
    /// V1/V2.x default (anonymous → Bearer); <c>Basic</c> for Nexus / Gitea
    /// Packages; <c>AwsEcr</c> for AWS Elastic Container Registry.</summary>
    RegistryAuthType RegistryAuthType,
    /// <summary>V2.4 — true when both AWS access key id and secret are
    /// configured server-side. The values themselves are never returned.</summary>
    bool HasAwsCredentials,
    /// <summary>V2.4 — AWS region for ECR (plaintext, e.g. <c>eu-central-1</c>).
    /// Not a secret — used by the UI to pre-fill the region picker.</summary>
    string? AwsRegion,
    bool UpdateNotificationsEnabled,
    bool TelegramNotificationsEnabled,
    CheckScheduleType ScheduleType,
    int CheckEveryHours,
    TimeOnly? CheckAtTime,
    DayOfWeek? CheckOnDayOfWeek,
    string? TagPatternFilter,
    DockerUpdateStatus UpdateStatus,
    string? CurrentDigest,
    string? LatestDigest,
    string? CurrentVersionTag,
    string? LatestVersionTag,
    /// <summary>V2.3 — GitHub release web URL for <see cref="LatestVersionTag"/>
    /// when the registry is GHCR and a matching release exists.</summary>
    string? LatestReleaseUrl,
    /// <summary>V2.3 — release notes body (markdown) for the same tag,
    /// truncated to 2 000 chars by the GitHub client.</summary>
    string? LatestReleaseBody,
    DateTime? LastCheckedUtc,
    DateTime? LastUpdateDetectedUtc,
    DateTime? NextCheckUtc,
    string? LastError,
    /// <summary>V2.6 — per-watch webhook URL token (64-char hex). <c>null</c>
    /// when the user hasn't opted in to webhook delivery. The URL is
    /// composed by the frontend from this token plus the app base URL.</summary>
    string? WebhookToken,
    /// <summary>V2.6 — UTC timestamp of the most recent accepted webhook
    /// delivery for this watch, or <c>null</c> if none.</summary>
    DateTime? LastWebhookReceivedUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Create-or-update payload for a watch on the service's existing Docker
/// connection. The image reference is validated by parsing it at controller
/// level; the parsed registry/repository/tag are stored alongside for the
/// background scan.
/// </summary>
public sealed record DockerWatchUpsertRequest(
    [Required, MaxLength(100)] string Label,
    bool Enabled,
    [Required, MaxLength(500)] string ImageReference,
    [Required, MaxLength(200)] string ContainerName,
    SecretValueUpsert? RegistryUsername,
    SecretValueUpsert? RegistryPassword,
    /// <summary>V2.4 — authentication strategy for the registry. Default
    /// <see cref="RegistryAuthType.Auto"/> preserves V1/V2.x behaviour.
    /// The mapper auto-promotes to <see cref="RegistryAuthType.AwsEcr"/>
    /// when the registry hostname matches the ECR pattern unless the user
    /// explicitly overrides.</summary>
    RegistryAuthType RegistryAuthType = RegistryAuthType.Auto,
    /// <summary>V2.4 — AWS access key id (tri-state secret) for ECR.</summary>
    SecretValueUpsert? AwsAccessKeyId = null,
    /// <summary>V2.4 — AWS secret access key (tri-state secret) for ECR.</summary>
    SecretValueUpsert? AwsSecretAccessKey = null,
    /// <summary>V2.4 — AWS region for ECR (plaintext, e.g. <c>eu-central-1</c>).</summary>
    [MaxLength(50)] string? AwsRegion = null,
    bool UpdateNotificationsEnabled = true,
    bool TelegramNotificationsEnabled = false,
    /// <summary>V2.2 — schedule mode. Default <see cref="CheckScheduleType.Hourly"/>.</summary>
    CheckScheduleType ScheduleType = CheckScheduleType.Hourly,
    /// <summary>V2.2 — only honoured when <see cref="ScheduleType"/> is
    /// <see cref="CheckScheduleType.Hourly"/>. Allowed: 1, 2, 4, 6, 12, 24.
    /// Default 24.</summary>
    int CheckEveryHours = 24,
    /// <summary>V2.2 — required when <see cref="ScheduleType"/> is Daily or
    /// Weekly. Stored as UTC.</summary>
    TimeOnly? CheckAtTime = null,
    /// <summary>V2.2 — required when <see cref="ScheduleType"/> is Weekly.</summary>
    DayOfWeek? CheckOnDayOfWeek = null,
    /// <summary>V2.1 optional .NET regex applied to the registry's tag list when
    /// the checker resolves "latest". <c>null</c> or empty preserves V1 behaviour
    /// (compare the pinned tag's digest directly).</summary>
    [MaxLength(200)] string? TagPatternFilter = null,
    /// <summary>V2.3 — optional GitHub PAT used by release-notes enrichment
    /// for GHCR images. Same tri-state convention as the registry secrets so
    /// an edit can keep the encrypted value without round-tripping it.</summary>
    SecretValueUpsert? GitHubPat = null,
    /// <summary>V3.6 — optional service to link this tracked container to.
    /// <c>null</c> leaves the container standalone. The connection-scoped
    /// controller validates that the service belongs to the same user; the
    /// legacy service-scoped controller ignores this field (the route already
    /// pins the service).</summary>
    Guid? WebResourceId = null);

/// <summary>
/// Per-watch reachability probe — verifies the configured registry is reachable
/// for the watch's image and that the container exists on the service's Docker
/// connection. Distinct from the connection-level ping which only tests the
/// daemon.
/// </summary>
public sealed record DockerWatchTestRequest(
    [Required, MaxLength(500)] string ImageReference,
    [Required, MaxLength(200)] string ContainerName,
    SecretValueUpsert? RegistryUsername,
    SecretValueUpsert? RegistryPassword,
    [MaxLength(200)] string? TagPatternFilter = null,
    /// <summary>V2.3 — optional GitHub PAT, mirroring the upsert request.</summary>
    SecretValueUpsert? GitHubPat = null,
    /// <summary>V2.4 — registry auth strategy mirroring the upsert request.</summary>
    RegistryAuthType RegistryAuthType = RegistryAuthType.Auto,
    /// <summary>V2.4 — AWS access key id (tri-state secret) for ECR.</summary>
    SecretValueUpsert? AwsAccessKeyId = null,
    /// <summary>V2.4 — AWS secret access key (tri-state secret) for ECR.</summary>
    SecretValueUpsert? AwsSecretAccessKey = null,
    /// <summary>V2.4 — AWS region for ECR.</summary>
    [MaxLength(50)] string? AwsRegion = null);

public sealed record DockerWatchTestResponse(
    bool DockerHostReachable,
    bool ContainerFound,
    bool RegistryReachable,
    string? Error);

// ── V2.7 — Auto-update ("Update now") ──────────────────────────────────────

/// <summary>
/// V2.7 — response returned by <c>POST /watches/{id}/update</c>. Carries
/// the audit row that the update wrote to <c>DockerUpdateAttempts</c>
/// plus the refreshed watch so the UI can replace both in a single
/// round-trip.
/// </summary>
public sealed record DockerWatchUpdateResponse(
    DockerUpdateAttemptResponse Attempt,
    DockerWatchResponse Watch);

/// <summary>
/// V2.7 — a single row in the per-watch "Update history" panel. Read
/// model only — the history is append-only and rows are immutable.
/// </summary>
public sealed record DockerUpdateAttemptResponse(
    Guid Id,
    /// <summary>V2.7: always populated. V3.5: nullable — instance-page
    /// actions (Start / Stop / Restart / Remove) aren't tied to a watch.</summary>
    Guid? DockerWatchId,
    /// <summary>V2.7: always populated. V3.5: nullable — instance-page
    /// actions don't have a parent service.</summary>
    Guid? WebResourceId,
    /// <summary>V3.5 — the Docker connection the action was performed
    /// against. Nullable for back-compat with pre-V3.5 rows.</summary>
    Guid? DockerConnectionId,
    /// <summary>V3.5 — Docker container id at action time. Captured
    /// alongside <see cref="ContainerName"/> for cross-referencing if the
    /// container is renamed or recreated.</summary>
    string? ContainerId,
    /// <summary>V3.5 — kind of lifecycle action this row represents
    /// (Update / Start / Stop / Restart / Remove). V2.7 rows default to
    /// <c>Update</c>.</summary>
    DockerContainerActionType ActionType,
    DockerUpdateAttemptStatus Status,
    string ImageReference,
    string ContainerName,
    string? PreviousDigest,
    string? NewDigest,
    string? Error,
    DateTime CompletedUtc,
    DateTime CreatedUtc,
    /// <summary>V3.2 — true when the recreated container reported a
    /// <c>healthy</c> Docker healthcheck state (or has no healthcheck and
    /// stayed running) within the post-start verification window. Always
    /// false on non-success statuses, and false on a Success status only
    /// if the verification step was skipped (max-attempts = 0).</summary>
    bool HealthVerified,
    /// <summary>V3.2 — UTC timestamp the recreated container was observed
    /// healthy. <c>null</c> on every non-verified attempt.</summary>
    DateTime? HealthVerifiedUtc);
