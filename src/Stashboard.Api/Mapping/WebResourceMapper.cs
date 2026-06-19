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
    public Task<WebResourceResponse> MapAsync(WebResourceEntity entity, CancellationToken cancellationToken)
        => MapAsync(entity, null, cancellationToken);

    public async Task<WebResourceResponse> MapAsync(
        WebResourceEntity entity,
        IReadOnlyDictionary<(Guid ProxmoxConnectionId, int VmId), ProxmoxGuestEntity>? proxmoxGuests,
        CancellationToken cancellationToken)
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

        var (proxmoxStatus, linkedGuests) = MapProxmoxLinks(entity, proxmoxGuests);

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
                .ToList(),
            proxmoxStatus,
            linkedGuests,
            entity.ProxmoxConnectionId);
    }

    /// <summary>
    /// V7.9 — resolves the service's Proxmox guest links into the aggregated
    /// update badge + the per-guest summary list. Links whose guest row is absent
    /// from <paramref name="proxmoxGuests"/> (no lookup supplied, or the guest was
    /// destroyed) are dropped from both. Summaries are ordered by name for a
    /// stable UI.
    /// </summary>
    private static (ProxmoxUpdateStatus? Status, IReadOnlyList<LinkedProxmoxGuestSummary> Guests) MapProxmoxLinks(
        WebResourceEntity entity,
        IReadOnlyDictionary<(Guid ProxmoxConnectionId, int VmId), ProxmoxGuestEntity>? proxmoxGuests)
    {
        if (entity.ProxmoxGuestLinks.Count == 0 || proxmoxGuests is null)
            return (null, []);

        var summaries = new List<LinkedProxmoxGuestSummary>(entity.ProxmoxGuestLinks.Count);
        foreach (var link in entity.ProxmoxGuestLinks)
        {
            if (!proxmoxGuests.TryGetValue((link.ProxmoxConnectionId, link.VmId), out var guest))
                continue; // guest destroyed / not yet discovered — drop the link from the badge

            summaries.Add(new LinkedProxmoxGuestSummary(
                link.ProxmoxConnectionId,
                guest.VmId,
                guest.Name,
                guest.GuestType,
                guest.IsRunning,
                guest.PendingUpdates,
                guest.MonitoringEnabled,
                GuestUpdateStatus(guest),
                guest.LastCheckedUtc));
        }

        if (summaries.Count == 0) return (null, []);
        summaries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return (AggregateProxmoxStatus(summaries), summaries);
    }

    /// <summary>
    /// Per-guest update state. A monitoring-off or snoozed guest is
    /// <c>Disabled</c>; otherwise a positive pending count is
    /// <c>UpdateAvailable</c>, zero is <c>UpToDate</c>, and a null count
    /// (stopped / VM / never-checked / probe failed) is <c>Error</c> when the last
    /// check recorded one, else <c>Unknown</c>.
    /// </summary>
    private static ProxmoxUpdateStatus GuestUpdateStatus(ProxmoxGuestEntity guest)
    {
        var snoozed = guest.MonitoringSnoozedUntil is { } until && until > DateTime.UtcNow;
        if (!guest.MonitoringEnabled || snoozed) return ProxmoxUpdateStatus.Disabled;
        if (guest.PendingUpdates is > 0) return ProxmoxUpdateStatus.UpdateAvailable;
        if (guest.PendingUpdates == 0) return ProxmoxUpdateStatus.UpToDate;
        return string.IsNullOrEmpty(guest.LastError)
            ? ProxmoxUpdateStatus.Unknown
            : ProxmoxUpdateStatus.Error;
    }

    /// <summary>
    /// Reduces N linked guests' statuses to the one value driving the service
    /// card's Proxmox badge, with the same actionable-first precedence as
    /// <see cref="AggregateDockerStatus"/>: <c>UpdateAvailable</c> > <c>Error</c> >
    /// <c>UpToDate</c> > <c>Disabled</c> > <c>Unknown</c>.
    /// </summary>
    private static ProxmoxUpdateStatus AggregateProxmoxStatus(IReadOnlyList<LinkedProxmoxGuestSummary> guests)
    {
        if (guests.Any(g => g.UpdateStatus == ProxmoxUpdateStatus.UpdateAvailable)) return ProxmoxUpdateStatus.UpdateAvailable;
        if (guests.Any(g => g.UpdateStatus == ProxmoxUpdateStatus.Error)) return ProxmoxUpdateStatus.Error;
        if (guests.All(g => g.UpdateStatus == ProxmoxUpdateStatus.UpToDate)) return ProxmoxUpdateStatus.UpToDate;
        if (guests.All(g => g.UpdateStatus == ProxmoxUpdateStatus.Disabled)) return ProxmoxUpdateStatus.Disabled;
        return ProxmoxUpdateStatus.Unknown;
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
