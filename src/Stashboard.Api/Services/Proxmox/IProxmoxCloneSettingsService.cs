using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V8.0 — reads and writes the single app-wide clone/snapshot master switch. The
/// row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxClone"/>
/// config flag, so a deployment that set the flag on first run keeps that value
/// until an operator changes it on the Settings page. Mirrors
/// <see cref="IProxmoxCreateSettingsService"/>.
/// </summary>
public interface IProxmoxCloneSettingsService
{
    /// <summary>Whether clone/snapshot is enabled server-wide. This is the global
    /// gate the clone and snapshot write endpoints check.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ProxmoxCloneSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateProxmoxCloneSettingsRequest request, CancellationToken cancellationToken = default);
}
