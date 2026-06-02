using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.ContainerExec;

/// <summary>
/// V5.7 — reads and writes the single app-wide container-exec master switch.
/// The row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowContainerExec"/>
/// config flag, so a deployment that set the flag on first run keeps that value
/// until an operator changes it on the Settings page. Mirrors
/// <see cref="Stashboard.Api.Services.HostShell.IHostShellSettingsService"/>.
/// </summary>
public interface IContainerExecSettingsService
{
    /// <summary>Whether container exec is enabled server-wide. This is the
    /// global gate the ticket / WebSocket endpoints check.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ContainerExecSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateContainerExecSettingsRequest request, CancellationToken cancellationToken = default);
}
