using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — reads and writes the single app-wide Proxmox-console master switch.
/// The row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxConsole"/>
/// config flag, so a deployment that set the flag on first run keeps that value
/// until an operator changes it on the Settings page. Mirrors
/// <see cref="Stashboard.Api.Services.ContainerExec.IContainerExecSettingsService"/>.
/// </summary>
public interface IProxmoxConsoleSettingsService
{
    /// <summary>Whether the LXC console is enabled server-wide. This is the
    /// global gate the ticket / WebSocket endpoints check.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ProxmoxConsoleSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateProxmoxConsoleSettingsRequest request, CancellationToken cancellationToken = default);
}
