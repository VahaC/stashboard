using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Services.Templates;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V7.5 — serves the read-only starter-recipe catalogue behind the "From
/// template" tab of the new-project wizard. The catalogue is shipped with the
/// image (plus an optional user override directory) and loaded once at startup;
/// the client renders the cards, collects the per-deployment variables, resolves
/// the <c>${KEY}</c> placeholders and posts the result to the existing
/// <c>create-project</c> endpoint. Path: <c>/api/templates</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/templates")]
public class TemplatesController(IServiceTemplateCatalog catalog) : ControllerBase
{
    /// <summary>V7.5 — all available service templates, sorted by category then name.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ServiceTemplate>> List() => Ok(catalog.GetTemplates());
}
