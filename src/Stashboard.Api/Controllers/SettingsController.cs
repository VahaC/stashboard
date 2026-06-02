using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Api.Services.HostShell;
using Stashboard.Api.Services.ImagePrune;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V5.3 — app-wide operational settings managed from the UI Settings page
/// (rather than env vars / appsettings). Path: <c>/api/settings</c>.
/// </summary>
/// <remarks>
/// App-wide singletons follow the same posture as the email settings
/// (<c>/api/account/email-settings</c>): authenticated, but not scoped to a
/// single user — Stashboard has no role system, so any signed-in operator can
/// read and change them.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(
    IHostShellSettingsService hostShell,
    IContainerExecSettingsService containerExec,
    IImagePruneSettingsService imagePrune,
    IHealthCheckSettingsService healthCheck) : ControllerBase
{
    /// <summary>The host-terminal master switch (the global gate for V5.3).</summary>
    [HttpGet("host-shell")]
    public async Task<ActionResult<HostShellSettingsResponse>> GetHostShell(CancellationToken cancellationToken)
        => Ok(await hostShell.GetAsync(cancellationToken));

    [HttpPut("host-shell")]
    public async Task<IActionResult> UpdateHostShell(
        [FromBody] UpdateHostShellSettingsRequest request, CancellationToken cancellationToken)
    {
        await hostShell.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V5.7 — the container-exec master switch (the global gate for V5.7).</summary>
    [HttpGet("container-exec")]
    public async Task<ActionResult<ContainerExecSettingsResponse>> GetContainerExec(CancellationToken cancellationToken)
        => Ok(await containerExec.GetAsync(cancellationToken));

    [HttpPut("container-exec")]
    public async Task<IActionResult> UpdateContainerExec(
        [FromBody] UpdateContainerExecSettingsRequest request, CancellationToken cancellationToken)
    {
        await containerExec.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V5.5 — the image-prune master switch + sweep interval.</summary>
    [HttpGet("image-prune")]
    public async Task<ActionResult<ImagePruneSettingsResponse>> GetImagePrune(CancellationToken cancellationToken)
        => Ok(await imagePrune.GetAsync(cancellationToken));

    [HttpPut("image-prune")]
    public async Task<IActionResult> UpdateImagePrune(
        [FromBody] UpdateImagePruneSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        await imagePrune.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V5.6 — the offline-alert tuning: failure threshold + in-probe retries.</summary>
    [HttpGet("health-check")]
    public async Task<ActionResult<HealthCheckSettingsResponse>> GetHealthCheck(CancellationToken cancellationToken)
        => Ok(await healthCheck.GetAsync(cancellationToken));

    [HttpPut("health-check")]
    public async Task<IActionResult> UpdateHealthCheck(
        [FromBody] UpdateHealthCheckSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        await healthCheck.UpdateAsync(request, cancellationToken);
        return NoContent();
    }
}
