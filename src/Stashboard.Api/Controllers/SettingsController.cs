using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Api.Services.HostShell;
using Stashboard.Api.Services.ImagePrune;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Api.Services.ProxmoxConsole;

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
    IProxmoxConsoleSettingsService proxmoxConsole,
    IProxmoxUpdateApplySettingsService proxmoxUpdates,
    IProxmoxDestroySettingsService proxmoxDestroy,
    IProxmoxCreateSettingsService proxmoxCreate,
    IProxmoxCloneSettingsService proxmoxClone,
    IProxmoxRestoreSettingsService proxmoxRestore,
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

    /// <summary>V6.6 — the LXC-console master switch (the global gate for V6.6).</summary>
    [HttpGet("proxmox-console")]
    public async Task<ActionResult<ProxmoxConsoleSettingsResponse>> GetProxmoxConsole(CancellationToken cancellationToken)
        => Ok(await proxmoxConsole.GetAsync(cancellationToken));

    [HttpPut("proxmox-console")]
    public async Task<IActionResult> UpdateProxmoxConsole(
        [FromBody] UpdateProxmoxConsoleSettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxConsole.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V6.7.1 — the Proxmox "Update now" master switch (the global gate for V6.7.1).</summary>
    [HttpGet("proxmox-updates")]
    public async Task<ActionResult<ProxmoxUpdateApplySettingsResponse>> GetProxmoxUpdates(CancellationToken cancellationToken)
        => Ok(await proxmoxUpdates.GetAsync(cancellationToken));

    [HttpPut("proxmox-updates")]
    public async Task<IActionResult> UpdateProxmoxUpdates(
        [FromBody] UpdateProxmoxUpdateApplySettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxUpdates.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V6.13 — the destroy-LXC master switch (the global gate for V6.13).</summary>
    [HttpGet("proxmox-destroy")]
    public async Task<ActionResult<ProxmoxDestroySettingsResponse>> GetProxmoxDestroy(CancellationToken cancellationToken)
        => Ok(await proxmoxDestroy.GetAsync(cancellationToken));

    [HttpPut("proxmox-destroy")]
    public async Task<IActionResult> UpdateProxmoxDestroy(
        [FromBody] UpdateProxmoxDestroySettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxDestroy.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V6.13.1 — the create-LXC master switch (the global gate for V6.13.1).</summary>
    [HttpGet("proxmox-create")]
    public async Task<ActionResult<ProxmoxCreateSettingsResponse>> GetProxmoxCreate(CancellationToken cancellationToken)
        => Ok(await proxmoxCreate.GetAsync(cancellationToken));

    [HttpPut("proxmox-create")]
    public async Task<IActionResult> UpdateProxmoxCreate(
        [FromBody] UpdateProxmoxCreateSettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxCreate.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V8.0 — the clone/snapshot master switch (the global gate for V8.0).</summary>
    [HttpGet("proxmox-clone")]
    public async Task<ActionResult<ProxmoxCloneSettingsResponse>> GetProxmoxClone(CancellationToken cancellationToken)
        => Ok(await proxmoxClone.GetAsync(cancellationToken));

    [HttpPut("proxmox-clone")]
    public async Task<IActionResult> UpdateProxmoxClone(
        [FromBody] UpdateProxmoxCloneSettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxClone.UpdateAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>V8.1 — the restore-LXC master switch (the global gate for V8.1).</summary>
    [HttpGet("proxmox-restore")]
    public async Task<ActionResult<ProxmoxRestoreSettingsResponse>> GetProxmoxRestore(CancellationToken cancellationToken)
        => Ok(await proxmoxRestore.GetAsync(cancellationToken));

    [HttpPut("proxmox-restore")]
    public async Task<IActionResult> UpdateProxmoxRestore(
        [FromBody] UpdateProxmoxRestoreSettingsRequest request, CancellationToken cancellationToken)
    {
        await proxmoxRestore.UpdateAsync(request, cancellationToken);
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
