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
    IReadOnlyList<LinkedDockerWatchSummary>? LinkedDockerWatches = null);

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
    Guid? DockerConnectionId = null);

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
