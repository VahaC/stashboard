using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Services.StatusPages;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.2 — the public, unauthenticated status-page read. Returns a published page's live status
/// + uptime history at <c>GET /api/status/{slug}</c>, exposing only whitelisted display fields
/// (see <see cref="PublicStatusServiceResponse"/>) — never the owner's URLs, credentials, notes,
/// categories, tags or Docker/Proxmox internals.
/// <para>
/// An unpublished or unknown slug 404s — identically — so unpublished pages can't be enumerated.
/// The endpoint is IP-rate-limited (the <c>public-status</c> policy) and sends a short cache
/// header so a widely-shared page can't be used to hammer the instance.
/// </para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/status")]
[EnableRateLimiting("public-status")]
public class PublicStatusController(ApplicationDbContext db, IPublicStatusPageBuilder builder) : ControllerBase
{
    /// <summary>Public cache lifetime (seconds). Long enough to absorb a burst of shares, short
    /// enough that a status flip still surfaces quickly.</summary>
    private const int CacheSeconds = 30;

    [HttpGet("{slug}")]
    public async Task<ActionResult<PublicStatusPageResponse>> Get(string slug, CancellationToken cancellationToken)
    {
        if (!StatusPageSlug.IsValid(slug))
            return NotFound();

        var page = await db.StatusPages.AsNoTracking()
            .Include(p => p.Items).ThenInclude(i => i.WebResource)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsPublished, cancellationToken);
        if (page is null)
            return NotFound();

        Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";
        return Ok(await builder.BuildAsync(page, cancellationToken));
    }
}
