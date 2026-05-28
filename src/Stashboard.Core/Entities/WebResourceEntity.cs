using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

public class WebResourceEntity : AuditableEntity
{
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    [Required, MaxLength(500)]
    public string MainUrl { get; set; } = default!;

    public bool MainUrlHealthCheckEnabled { get; set; } = true;

    [MaxLength(500)]
    public string? AdditionalUrl { get; set; }

    public bool AdditionalUrlHealthCheckEnabled { get; set; } = true;

    public bool OfflineNotificationsEnabled { get; set; } = true;

    [MaxLength(500)]
    public string? HealthCheckUrl { get; set; }

    public HealthCheckMethod HealthCheckMethod { get; set; } = HealthCheckMethod.Get;

    /// <summary>Optional expected status range, e.g. "200-299" or "204". Null = 200-399.</summary>
    [MaxLength(20)]
    public string? ExpectedStatusRange { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public Guid? CategoryId { get; set; }
    public CategoryEntity? Category { get; set; }

    public LogoSource LogoSource { get; set; } = LogoSource.AutoFavicon;

    [MaxLength(500)]
    public string? CustomLogoPath { get; set; }

    /// <summary>Base64-encoded data URI of the service logo/favicon stored in the database.
    /// Format: <c>data:&lt;mime&gt;;base64,&lt;data&gt;</c></summary>
    public string? LogoBase64 { get; set; }

    public ServiceStatus CurrentStatus { get; set; } = ServiceStatus.Unknown;
    public DateTime? LastCheckedUtc { get; set; }
    public int? LastResponseTimeMs { get; set; }
    public string? LastError { get; set; }

    /// <summary>Number of consecutive failed scans on the main URL. The status only flips to
    /// Down (and an alert fires) once this reaches the configured failure threshold; a single
    /// transient blip is absorbed and the counter resets on the next success.</summary>
    public int MainUrlConsecutiveFailures { get; set; }

    public ServiceStatus AdditionalUrlStatus { get; set; } = ServiceStatus.Unknown;
    public int? AdditionalUrlLastResponseTimeMs { get; set; }
    public string? AdditionalUrlLastError { get; set; }

    /// <summary>Consecutive-failure counter for the additional URL. See <see cref="MainUrlConsecutiveFailures"/>.</summary>
    public int AdditionalUrlConsecutiveFailures { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CredentialEntity> Credentials { get; set; } = new List<CredentialEntity>();
    public ICollection<WebResourceTagEntity> WebResourceTags { get; set; } = new List<WebResourceTagEntity>();

    /// <summary>Optional reference to a user-level <see cref="DockerConnectionEntity"/>.
    /// Required before any <see cref="DockerWatches"/> can be added — the watch
    /// list, the container picker, and the update-command generator all read the
    /// daemon transport from here. A single connection can be reused across many
    /// services hosted on the same daemon.</summary>
    public Guid? DockerConnectionId { get; set; }
    public DockerConnectionEntity? DockerConnection { get; set; }

    /// <summary>Docker watches attached to this service — one per container the user
    /// wants tracked. A composite service (e.g. WordPress = app container + MariaDB
    /// container) ends up with multiple entries. Empty when the user hasn't enabled
    /// Docker tracking. The dashboard aggregates these statuses to drive the
    /// "Update available" badge without a per-card fetch.</summary>
    public ICollection<DockerWatchEntity> DockerWatches { get; set; } = new List<DockerWatchEntity>();
}
