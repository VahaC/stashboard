using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.HealthCheckSettings;

/// <summary>
/// V5.6 — reads / writes the single app-wide health-check settings row.
/// Mirrors <see cref="Stashboard.Api.Services.ImagePrune.IImagePruneSettingsService"/>:
/// the row is created on first access from the bound
/// <see cref="Stashboard.Core.Options.HealthCheckOptions"/> config block so a
/// deployment that set the values on first run keeps them until an operator
/// changes them on the Settings → Health checks page.
/// </summary>
public interface IHealthCheckSettingsService
{
    /// <summary>Loads the current settings (creating the row on first access).</summary>
    Task<HealthCheckSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the threshold + retry knobs.</summary>
    Task UpdateAsync(UpdateHealthCheckSettingsRequest request, CancellationToken cancellationToken = default);
}
