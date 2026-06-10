using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V6.13 — reads and writes the single app-wide destroy-LXC master switch. The
/// row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxDestroy"/>
/// config flag, so a deployment that set the flag on first run keeps that value
/// until an operator changes it on the Settings page. Mirrors
/// <see cref="IProxmoxUpdateApplySettingsService"/>.
/// </summary>
public interface IProxmoxDestroySettingsService
{
    /// <summary>Whether destroy-LXC is enabled server-wide. This is the global
    /// gate the destroy endpoint checks.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ProxmoxDestroySettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateProxmoxDestroySettingsRequest request, CancellationToken cancellationToken = default);
}
