using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.ImagePrune;

/// <summary>
/// V5.5 — reads / writes the single app-wide image-prune settings row.
/// Mirrors <see cref="Stashboard.Api.Services.HostShell.IHostShellSettingsService"/>:
/// the row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.ImagePruneOptions"/> config block so a
/// deployment that set the values on first run keeps them until an operator
/// changes them on the Settings page.
/// </summary>
public interface IImagePruneSettingsService
{
    /// <summary>Loads the current settings (creating the row on first access).</summary>
    Task<ImagePruneSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the toggle + interval.</summary>
    Task UpdateAsync(UpdateImagePruneSettingsRequest request, CancellationToken cancellationToken = default);
}
