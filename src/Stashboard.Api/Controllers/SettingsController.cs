using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.HostShell;

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
public class SettingsController(IHostShellSettingsService hostShell) : ControllerBase
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
}
