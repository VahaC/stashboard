using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HostShell;
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
    IContainerExecSettingsService containerExecSettings) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<StashboardFeaturesResponse>> Get(CancellationToken cancellationToken) =>
        Ok(new StashboardFeaturesResponse(
            options.Value.AllowContainerRemoval,
            // V5.3 — host-terminal master switch is DB-backed and managed from
            // the Settings page, not a static config flag.
            await hostShellSettings.IsEnabledAsync(cancellationToken),
            // V5.7 — container-exec master switch, same DB-backed pattern.
            await containerExecSettings.IsEnabledAsync(cancellationToken)));
}
