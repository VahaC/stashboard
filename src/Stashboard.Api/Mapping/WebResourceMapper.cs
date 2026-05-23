using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Mapping;

/// <inheritdoc cref="IWebResourceMapper"/>
public sealed class WebResourceMapper(
    IEncryptionService encryption,
    IFaviconService favicon) : IWebResourceMapper
{
    public async Task<WebResourceResponse> MapAsync(WebResourceEntity entity, CancellationToken cancellationToken)
    {
        string? faviconUrl;
        string? customLogoPath;

        if (entity.LogoSource == LogoSource.Custom)
        {
            // Base64 takes priority; fall back to file path for existing records
            customLogoPath = entity.LogoBase64 ?? entity.CustomLogoPath;
            faviconUrl = null;
        }
        else
        {
            customLogoPath = null;
            if (!string.IsNullOrEmpty(entity.CustomLogoPath))
            {
                // Legacy: AutoFavicon source but a custom path is already stored — treat as custom.
                faviconUrl = null;
            }
            else
            {
                // Return stored base64 when available to avoid re-downloading on every response
                faviconUrl = entity.LogoBase64
                    ?? await favicon.ResolveFaviconUrlAsync(entity.MainUrl, cancellationToken);
            }
        }

        return new WebResourceResponse(
            entity.Id,
            entity.Name,
            entity.MainUrl,
            entity.MainUrlHealthCheckEnabled,
            entity.AdditionalUrl,
            entity.AdditionalUrlHealthCheckEnabled,
            entity.OfflineNotificationsEnabled,
            entity.HealthCheckUrl,
            entity.HealthCheckMethod,
            entity.ExpectedStatusRange,
            entity.Notes,
            entity.CategoryId,
            entity.Category?.Name,
            entity.Category?.Color,
            entity.LogoSource,
            customLogoPath,
            faviconUrl,
            entity.CurrentStatus,
            entity.LastCheckedUtc,
            entity.LastResponseTimeMs,
            entity.LastError,
            entity.AdditionalUrlStatus,
            entity.AdditionalUrlLastResponseTimeMs,
            entity.AdditionalUrlLastError,
            entity.WebResourceTags.Select(st => st.Tag.Name).OrderBy(n => n).ToList(),
            entity.Credentials
                .Select(c => new CredentialDto(c.Id, c.Key, SafeDecrypt(c.EncryptedValue), c.IsSecret))
                .ToList(),
            entity.CreatedUtc,
            entity.UpdatedUtc,
            AggregateDockerStatus(entity.DockerWatches),
            entity.DockerConnectionId,
            entity.DockerWatches
                .OrderBy(w => w.Label)
                .Select(w => new LinkedDockerWatchSummary(
                    w.Id,
                    w.DockerConnectionId,
                    w.Label,
                    w.ContainerName,
                    w.ImageReference,
                    w.Enabled,
                    w.UpdateStatus,
                    w.LastCheckedUtc))
                .ToList());
    }

    private string SafeDecrypt(string ciphertext)
    {
        try { return encryption.Decrypt(ciphertext); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Reduces N watches' statuses to one value driving the dashboard's
    /// "Update available" badge. Precedence is actionable-first:
    /// <c>UpdateAvailable</c> > <c>Error</c> > <c>UpToDate</c> > <c>Disabled</c>
    /// > <c>Unknown</c>. Returns <c>null</c> when the service tracks no
    /// container.
    /// </summary>
    private static DockerUpdateStatus? AggregateDockerStatus(ICollection<DockerWatchEntity> watches)
    {
        if (watches.Count == 0) return null;
        if (watches.Any(w => w.UpdateStatus == DockerUpdateStatus.UpdateAvailable)) return DockerUpdateStatus.UpdateAvailable;
        if (watches.Any(w => w.UpdateStatus == DockerUpdateStatus.Error)) return DockerUpdateStatus.Error;
        if (watches.All(w => w.UpdateStatus == DockerUpdateStatus.UpToDate)) return DockerUpdateStatus.UpToDate;
        if (watches.All(w => w.UpdateStatus == DockerUpdateStatus.Disabled)) return DockerUpdateStatus.Disabled;
        return DockerUpdateStatus.Unknown;
    }
}
