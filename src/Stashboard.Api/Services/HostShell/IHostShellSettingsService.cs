using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.HostShell;

/// <summary>
/// V5.3 — reads and writes the single app-wide host-terminal master switch.
/// The row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowHostShell"/> config
/// flag, so a deployment that set the flag on first run keeps that value until
/// an operator changes it on the Settings page. Mirrors
/// <see cref="Stashboard.Api.Notifications.IEmailSettingsService"/>.
/// </summary>
public interface IHostShellSettingsService
{
    /// <summary>Whether the host terminal is enabled server-wide. This is the
    /// global gate the ticket / WebSocket endpoints check.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<HostShellSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateHostShellSettingsRequest request, CancellationToken cancellationToken = default);
}
