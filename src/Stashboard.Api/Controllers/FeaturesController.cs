using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HostShell;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V3.5 — exposes the small set of server-side feature flags the
/// frontend needs to gate UI affordances (e.g. hide the Remove
/// container button when <see cref="StashboardOptions.AllowContainerRemoval"/>
/// is off). Authenticated but otherwise unscoped — flag state is the
/// same for every user.
/// </summary>
[ApiController]
[Authorize]
[Route("api/features")]
public class FeaturesController(
    IOptions<StashboardOptions> options,
    IHostShellSettingsService hostShellSettings,
    IContainerExecSettingsService containerExecSettings,
    IProxmoxConsoleSettingsService proxmoxConsoleSettings,
    IProxmoxUpdateApplySettingsService proxmoxUpdatesSettings,
    IProxmoxDestroySettingsService proxmoxDestroySettings,
    IProxmoxCreateSettingsService proxmoxCreateSettings,
    IProxmoxCloneSettingsService proxmoxCloneSettings) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<StashboardFeaturesResponse>> Get(CancellationToken cancellationToken) =>
        Ok(new StashboardFeaturesResponse(
            options.Value.AllowContainerRemoval,
            // V5.3 — host-terminal master switch is DB-backed and managed from
            // the Settings page, not a static config flag.
            await hostShellSettings.IsEnabledAsync(cancellationToken),
            // V5.7 — container-exec master switch, same DB-backed pattern.
            await containerExecSettings.IsEnabledAsync(cancellationToken),
            // V6.6 — LXC-console master switch, same DB-backed pattern.
            await proxmoxConsoleSettings.IsEnabledAsync(cancellationToken),
            // V6.7.1 — Proxmox "Update now" master switch, same DB-backed pattern.
            await proxmoxUpdatesSettings.IsEnabledAsync(cancellationToken),
            // V6.13 — destroy-LXC master switch, same DB-backed pattern.
            await proxmoxDestroySettings.IsEnabledAsync(cancellationToken),
            // V6.13.1 — create-LXC master switch, same DB-backed pattern.
            await proxmoxCreateSettings.IsEnabledAsync(cancellationToken),
            // V8.0 — clone/snapshot master switch, same DB-backed pattern.
            await proxmoxCloneSettings.IsEnabledAsync(cancellationToken)));
}
