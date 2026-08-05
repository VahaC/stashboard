using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V3.6 — a tracked Docker container, treated as a first-class object owned by
/// its <see cref="DockerConnectionEntity"/> (the host it runs on). Host
/// configuration lives on the connection — a watch only stores the container it
/// targets, the image reference used to query the registry, optional registry
/// credentials for private images, notification preferences, and status fields
/// written by the background update checker.
/// <para>
/// The link to a <see cref="WebResourceEntity"/> (service) is now optional: a
/// container can be tracked without belonging to any service, and a service is
/// merely an optional grouping/label. One watch per
/// (<see cref="DockerConnectionId"/>, <see cref="ContainerName"/>).
/// </para>
/// </summary>
public class DockerWatchEntity : AuditableEntity
{
    /// <summary>The Docker host this container runs on. Required — a watch
    /// cannot exist without a daemon to query. The transport (socket / TLS /
    /// SSH) is read straight off the connection.</summary>
    public Guid DockerConnectionId { get; set; }
    public DockerConnectionEntity DockerConnection { get; set; } = default!;

    /// <summary>V3.6 — optional link to a service this container belongs to.
    /// <c>null</c> for a standalone container. Deleting the service sets this
    /// to <c>null</c> rather than removing the watch — the container lives on
    /// independently.</summary>
    public Guid? WebResourceId { get; set; }
    public WebResourceEntity? WebResource { get; set; }

    /// <summary>Denormalized owner id — mirrors the parent connection's
    /// <see cref="DockerConnectionEntity.UserId"/> so the background scan can
    /// query watches without a join.</summary>
    public Guid UserId { get; set; }

    /// <summary>Human-readable label for the tracked container (e.g. <c>app</c>
    /// vs. <c>db</c>). Defaults to the container name. No longer required to be
    /// unique — the natural key is now
    /// (<see cref="DockerConnectionId"/>, <see cref="ContainerName"/>).</summary>
    [Required, MaxLength(100)]
    public string Label { get; set; } = default!;

    public bool Enabled { get; set; } = true;

    /// <summary>Raw user input (e.g. <c>ghcr.io/owner/repo:tag</c>). Parsed into the
    /// <see cref="RegistryHost"/>, <see cref="Repository"/>, <see cref="Tag"/> fields at save time.</summary>
    [Required, MaxLength(500)]
    public string ImageReference { get; set; } = default!;

    [Required, MaxLength(200)]
    public string RegistryHost { get; set; } = default!;

    [Required, MaxLength(300)]
    public string Repository { get; set; } = default!;

    [Required, MaxLength(100)]
    public string Tag { get; set; } = default!;

    [Required, MaxLength(200)]
    public string ContainerName { get; set; } = default!;

    /// <summary>Encrypted registry credentials for private images. Both fields must be set together.</summary>
    public string? RegistryUsernameEncrypted { get; set; }
    public string? RegistryPasswordEncrypted { get; set; }

    /// <summary>
    /// V2.3 — encrypted GitHub PAT used by the release-notes enrichment path
    /// when the registry host is <c>ghcr.io</c>. Optional even for GHCR
    /// because the GitHub Releases API is anonymous for public repositories.
    /// Required for private GHCR images and bumps the rate-limit ceiling
    /// from 60 → 5 000 requests / hour for everything else.
    /// </summary>
    public string? GitHubPatEncrypted { get; set; }

    /// <summary>
    /// V2.4 — explicit registry authentication strategy. <see cref="RegistryAuthType.Auto"/>
    /// preserves the V1 / V2.x behaviour (anonymous → Bearer). The mapper
    /// promotes this automatically to <see cref="RegistryAuthType.AwsEcr"/>
    /// when the registry hostname matches the ECR pattern, but the user can
    /// override.
    /// </summary>
    public RegistryAuthType RegistryAuthType { get; set; } = RegistryAuthType.Auto;

    /// <summary>V2.4 — encrypted AWS access key id for ECR. Only meaningful when
    /// <see cref="RegistryAuthType"/> is <see cref="RegistryAuthType.AwsEcr"/>.</summary>
    public string? AwsAccessKeyIdEncrypted { get; set; }

    /// <summary>V2.4 — encrypted AWS secret access key for ECR.</summary>
    public string? AwsSecretAccessKeyEncrypted { get; set; }

    /// <summary>V2.4 — AWS region for ECR (e.g. <c>eu-central-1</c>). Stored in
    /// plaintext — it's not a secret, just routing info — and required when
    /// <see cref="RegistryAuthType"/> is <see cref="RegistryAuthType.AwsEcr"/>.</summary>
    [MaxLength(50)]
    public string? AwsRegion { get; set; }

    public bool UpdateNotificationsEnabled { get; set; } = true;

    /// <summary>Send a Telegram message in addition to (or instead of) the email
    /// when a newer image digest is detected. Effective only when the owner has
    /// configured Telegram on the Account page.</summary>
    public bool TelegramNotificationsEnabled { get; set; } = false;

    /// <summary>V10.0 — fan this watch's update notification out through the app-wide
    /// Apprise channel as well. Effective only when the operator has configured Apprise
    /// on the Notifications page. Independent of the email/Telegram toggles.</summary>
    public bool AppriseNotificationsEnabled { get; set; } = false;

    /// <summary>V2.2 per-watch schedule mode. Default <see cref="CheckScheduleType.Hourly"/>
    /// with <see cref="CheckEveryHours"/> = 24, which keeps the watch well under the
    /// Docker Hub anonymous rate-limit for the typical single-watch case.</summary>
    public CheckScheduleType ScheduleType { get; set; } = CheckScheduleType.Hourly;

    /// <summary>Only used when <see cref="ScheduleType"/> is <see cref="CheckScheduleType.Hourly"/>.
    /// Allowed values: <c>1, 2, 4, 6, 12, 24</c>. Default <c>24</c>.</summary>
    public int CheckEveryHours { get; set; } = 24;

    /// <summary>Only used when <see cref="ScheduleType"/> is
    /// <see cref="CheckScheduleType.Daily"/> or <see cref="CheckScheduleType.Weekly"/>.
    /// Stored in UTC.</summary>
    public TimeOnly? CheckAtTime { get; set; }

    /// <summary>Only used when <see cref="ScheduleType"/> is
    /// <see cref="CheckScheduleType.Weekly"/>.</summary>
    public DayOfWeek? CheckOnDayOfWeek { get; set; }

    /// <summary>
    /// Optional .NET regex applied to the registry's tag list when the
    /// checker resolves the "latest" tag. Lets users skip pre-release tags
    /// (<c>-rc</c>, <c>-beta</c>, …) without changing the pinned
    /// <see cref="Tag"/>. <c>null</c> means "use the pinned tag's digest as-is"
    /// (V1 behaviour). When set, the checker lists matching tags, picks the
    /// highest semver (lexicographic fallback) and compares its digest.
    /// </summary>
    [MaxLength(200)]
    public string? TagPatternFilter { get; set; }

    public DockerUpdateStatus UpdateStatus { get; set; } = DockerUpdateStatus.Unknown;

    [MaxLength(100)]
    public string? CurrentDigest { get; set; }

    [MaxLength(100)]
    public string? LatestDigest { get; set; }

    [MaxLength(100)]
    public string? CurrentVersionTag { get; set; }

    [MaxLength(100)]
    public string? LatestVersionTag { get; set; }

    /// <summary>
    /// V2.3 — GitHub release web URL for <see cref="LatestVersionTag"/>,
    /// populated when the registry is <c>ghcr.io</c> and the owner repository
    /// has a matching release. <c>null</c> otherwise (anywhere from "image not
    /// on GHCR" to "release for this tag doesn't exist" to "GitHub API was
    /// unreachable on this tick" — enrichment is best-effort).
    /// </summary>
    [MaxLength(500)]
    public string? LatestReleaseUrl { get; set; }

    /// <summary>
    /// V2.3 — release notes body (markdown) for <see cref="LatestVersionTag"/>,
    /// truncated to 2 000 chars by <c>GitHubReleaseClient</c>. Mirrors the
    /// same nullable semantics as <see cref="LatestReleaseUrl"/>.
    /// </summary>
    public string? LatestReleaseBody { get; set; }

    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastUpdateDetectedUtc { get; set; }

    /// <summary>UTC timestamp of the most recent update-available notification. Used to suppress
    /// re-notification for the same <see cref="LatestDigest"/>.</summary>
    public DateTime? LastNotificationSentUtc { get; set; }

    /// <summary><see cref="LatestDigest"/> already emailed about — keyed throttle for the
    /// email channel. Stamped only after a successful send.</summary>
    [MaxLength(100)]
    public string? LastNotifiedDigest { get; set; }

    /// <summary>
    /// V2.6 — hex-encoded 32-byte random token that authenticates an inbound
    /// registry webhook for this watch. <c>null</c> means the user hasn't
    /// opted in to webhook delivery (default). When set, the public endpoint
    /// <c>POST /api/docker/webhooks/{watchToken}</c> enqueues an out-of-band
    /// check for this watch, bypassing the schedule. Rotating the token in
    /// the UI invalidates the old URL immediately.
    /// </summary>
    [MaxLength(64)]
    public string? WebhookToken { get; set; }

    /// <summary>V2.6 — UTC timestamp of the most recent successfully-accepted
    /// webhook delivery. Surfaced in the UI so the user can verify their
    /// registry is actually firing the webhook. <c>null</c> until the first
    /// hit lands.</summary>
    public DateTime? LastWebhookReceivedUtc { get; set; }

    /// <summary><see cref="LatestDigest"/> already Telegram-messaged about — independent
    /// throttle for the Telegram channel. Stamped only after a successful send so a
    /// transient Bot API failure doesn't permanently suppress the notification.</summary>
    [MaxLength(100)]
    public string? LastTelegramNotifiedDigest { get; set; }

    /// <summary>V10.0 — <see cref="LatestDigest"/> already sent through Apprise — independent
    /// throttle for the Apprise channel. Stamped only after a successful send so a transient
    /// Apprise outage doesn't permanently suppress the notification.</summary>
    [MaxLength(100)]
    public string? LastAppriseNotifiedDigest { get; set; }

    public string? LastError { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
