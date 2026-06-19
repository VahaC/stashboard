using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

public sealed record WebResourceResponse(
    Guid Id,
    string Name,
    string MainUrl,
    bool MainUrlHealthCheckEnabled,
    string? AdditionalUrl,
    bool AdditionalUrlHealthCheckEnabled,
    bool OfflineNotificationsEnabled,
    string? HealthCheckUrl,
    HealthCheckMethod HealthCheckMethod,
    string? ExpectedStatusRange,
    string? Notes,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    LogoSource LogoSource,
    string? CustomLogoPath,
    string? FaviconUrl,
    ServiceStatus CurrentStatus,
    DateTime? LastCheckedUtc,
    int? LastResponseTimeMs,
    string? LastError,
    ServiceStatus AdditionalUrlStatus,
    int? AdditionalUrlLastResponseTimeMs,
    string? AdditionalUrlLastError,
    List<string> Tags,
    List<CredentialDto> Credentials,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    /// <summary>
    /// Docker watch <see cref="DockerUpdateStatus"/> when the service has Docker
    /// tracking configured, otherwise null. Lets the dashboard render an
    /// "Update available" badge without a per-card fetch.
    /// </summary>
    DockerUpdateStatus? DockerUpdateStatus = null,
    /// <summary>Reference to the user-level Docker connection this service is
    /// assigned to, or null when no connection has been picked yet.</summary>
    Guid? DockerConnectionId = null,
    /// <summary>V3.6 — read-only summary of the containers (watches) linked to
    /// this service. The service modal renders these as deep links into the
    /// Docker page where the tracking is actually managed. Empty when the
    /// service has no linked containers.</summary>
    IReadOnlyList<LinkedDockerWatchSummary>? LinkedDockerWatches = null,
    /// <summary>V7.9 — aggregated <see cref="ProxmoxUpdateStatus"/> of the Proxmox
    /// guests linked to this service, or null when none are linked. Surfaced
    /// alongside <see cref="DockerUpdateStatus"/> so the card can show both
    /// update badges at once.</summary>
    ProxmoxUpdateStatus? ProxmoxUpdateStatus = null,
    /// <summary>V7.9 — read-only summary of the Proxmox guests (LXC / VM) linked
    /// to this service. The service modal lists these with an unlink action; the
    /// detail comes from the guests' existing scan data. Empty when none are
    /// linked.</summary>
    IReadOnlyList<LinkedProxmoxGuestSummary>? LinkedProxmoxGuests = null,
    /// <summary>V7.9 — the user-level Proxmox connection this service is associated
    /// with (the "Proxmox host" picker), or null. The analogue of
    /// <see cref="DockerConnectionId"/>.</summary>
    Guid? ProxmoxConnectionId = null);

/// <summary>
/// V7.9 — compact projection of a Proxmox guest linked to a service. Carries the
/// guest's identity (by stable <c>(connection, vmid)</c> key) plus the scan-derived
/// state the modal renders (live state pill + pending-update count + per-guest
/// update status). The full guest is managed on the Proxmox page.
/// </summary>
public sealed record LinkedProxmoxGuestSummary(
    Guid ProxmoxConnectionId,
    int VmId,
    string Name,
    ProxmoxGuestType GuestType,
    bool IsRunning,
    int? PendingUpdates,
    bool MonitoringEnabled,
    ProxmoxUpdateStatus UpdateStatus,
    DateTime? LastCheckedUtc);

/// <summary>V7.9 — link one Proxmox guest (by <c>(connection, vmid)</c>) to a
/// service. The node row (<c>VmId == 0</c>) is rejected — service links are
/// guests only.</summary>
public sealed record ProxmoxGuestLinkRequest(Guid ProxmoxConnectionId, int VmId);

/// <summary>
/// V3.6 — compact, read-only projection of a <c>DockerWatch</c> linked to a
/// service. Carries just enough to render a status chip and a deep link to the
/// container on the Docker page (<c>/docker</c>); the full tracking settings
/// are edited there, not in the service modal.
/// </summary>
public sealed record LinkedDockerWatchSummary(
    Guid Id,
    Guid DockerConnectionId,
    string Label,
    string ContainerName,
    string ImageReference,
    bool Enabled,
    DockerUpdateStatus UpdateStatus,
    DateTime? LastCheckedUtc);

public sealed record CredentialDto(Guid Id, string Key, string Value, bool IsSecret);

public sealed record WebResourceUpsertRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(500)] string MainUrl,
    bool MainUrlHealthCheckEnabled,
    [MaxLength(500)] string? AdditionalUrl,
    bool AdditionalUrlHealthCheckEnabled,
    [MaxLength(500), Url] string? HealthCheckUrl,
    HealthCheckMethod HealthCheckMethod,
    [MaxLength(20)] string? ExpectedStatusRange,
    [MaxLength(2000)] string? Notes,
    Guid? CategoryId,
    LogoSource LogoSource,
    [MaxLength(500)] string? CustomLogoPath,
    List<string> Tags,
    List<CredentialUpsert> Credentials,
    bool OfflineNotificationsEnabled = true,
    /// <summary>Optional id of a user-level Docker connection to assign to the
    /// service. Setting it to null unassigns. The controller validates ownership.</summary>
    Guid? DockerConnectionId = null,
    /// <summary>V7.9 — optional id of a user-level Proxmox connection to associate
    /// with the service (the "Proxmox host" picker). Null unassigns. The controller
    /// validates ownership.</summary>
    Guid? ProxmoxConnectionId = null);

public sealed record CredentialUpsert(
    [Required, MaxLength(100)] string Key,
    string? Value,
    bool IsSecret);

public sealed record CategoryResponse(Guid Id, string Name, string Color, int ServiceCount);

public sealed record CategoryUpsertRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(7)] string Color);

public sealed record TagResponse(Guid Id, string Name, int ServiceCount);

public sealed record TagUpsertRequest([Required, MaxLength(50)] string Name);
