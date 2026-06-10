using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V6.7.1 — reads and writes the single app-wide Proxmox "Update now" master
/// switch. The row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxUpdates"/>
/// config flag, so a deployment that set the flag on first run keeps that value
/// until an operator changes it on the Settings page. Mirrors
/// <see cref="ProxmoxConsole.IProxmoxConsoleSettingsService"/>.
/// </summary>
public interface IProxmoxUpdateApplySettingsService
{
    /// <summary>Whether one-click Update now is enabled server-wide. This is the
    /// global gate the apply endpoints check.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ProxmoxUpdateApplySettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateProxmoxUpdateApplySettingsRequest request, CancellationToken cancellationToken = default);
}
