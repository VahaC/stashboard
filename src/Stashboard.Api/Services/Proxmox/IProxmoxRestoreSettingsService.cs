using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V8.1 — reads and writes the single app-wide restore-LXC master switch. The row is
/// created on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxRestore"/> config
/// flag, so a deployment that set the flag on first run keeps that value until an
/// operator changes it on the Settings page. Mirrors
/// <see cref="IProxmoxCreateSettingsService"/>.
/// </summary>
public interface IProxmoxRestoreSettingsService
{
    /// <summary>Whether restore-LXC is enabled server-wide. This is the global gate
    /// the restore endpoint checks.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Current setting for the Settings page.</summary>
    Task<ProxmoxRestoreSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle.</summary>
    Task UpdateAsync(UpdateProxmoxRestoreSettingsRequest request, CancellationToken cancellationToken = default);
}
