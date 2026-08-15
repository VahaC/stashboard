using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Services.StatusPages;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.2 — owner-scoped CRUD for public status pages (create / edit / publish / delete). The
/// public, unauthenticated read lives separately on <see cref="PublicStatusController"/>; this
/// controller never returns the public payload, only the management view of a page.
/// </summary>
[ApiController]
[Authorize]
[Route("api/status-pages")]
public class StatusPagesController(ApplicationDbContext db) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<StatusPageResponse>>> List(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var pages = await db.StatusPages.AsNoTracking()
            .Include(p => p.Items).ThenInclude(i => i.WebResource)
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Title)
            .ToListAsync(cancellationToken);
        return Ok(pages.Select(Map).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StatusPageResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var page = await LoadOwnedAsync(id, cancellationToken);
        return page is null ? NotFound() : Ok(Map(page));
    }

    [HttpPost]
    public async Task<ActionResult<StatusPageResponse>> Create([FromBody] StatusPageUpsertRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            return BadRequest(new { error = "A title is required." });

        var slug = await ResolveSlugAsync(request.Slug, title, excludeId: null, cancellationToken);
        if (slug is null)
            return BadRequest(new { error = "That link is already taken — choose a different slug." });

        var ownedServiceIds = await OwnedServiceIdsAsync(request.Items, cancellationToken);
        if (ownedServiceIds is null)
            return BadRequest(new { error = "One or more selected services do not exist." });

        var page = new StatusPageEntity
        {
            UserId = UserId,
            Title = title,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Slug = slug,
            IsPublished = request.IsPublished,
            UpdatedUtc = DateTime.UtcNow,
        };
        db.StatusPages.Add(page);
        ApplyItems(page.Id, request.Items, ownedServiceIds);
        await db.SaveChangesAsync(cancellationToken);

        var fresh = await LoadOwnedAsync(page.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = page.Id }, Map(fresh!));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<StatusPageResponse>> Update(Guid id, [FromBody] StatusPageUpsertRequest request, CancellationToken cancellationToken)
    {
        var page = await LoadOwnedAsync(id, cancellationToken);
        if (page is null) return NotFound();

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            return BadRequest(new { error = "A title is required." });

        var slug = await ResolveSlugAsync(request.Slug, title, excludeId: id, cancellationToken);
        if (slug is null)
            return BadRequest(new { error = "That link is already taken — choose a different slug." });

        var ownedServiceIds = await OwnedServiceIdsAsync(request.Items, cancellationToken);
        if (ownedServiceIds is null)
            return BadRequest(new { error = "One or more selected services do not exist." });

        page.Title = title;
        page.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        page.Slug = slug;
        page.IsPublished = request.IsPublished;
        page.UpdatedUtc = DateTime.UtcNow;

        db.StatusPageItems.RemoveRange(page.Items);
        ApplyItems(page.Id, request.Items, ownedServiceIds);

        await db.SaveChangesAsync(cancellationToken);

        var fresh = await LoadOwnedAsync(id, cancellationToken);
        return Ok(Map(fresh!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.Id == id && p.UserId == UserId, cancellationToken);
        if (page is null) return NotFound();
        db.StatusPages.Remove(page);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private Task<StatusPageEntity?> LoadOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.StatusPages
            .Include(p => p.Items).ThenInclude(i => i.WebResource)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, cancellationToken);
    }

    /// <summary>Resolve and validate the desired slug. Uses the supplied slug when valid, else
    /// derives one from the title. Returns null when the slug is taken by another page.</summary>
    private async Task<string?> ResolveSlugAsync(string? requested, string title, Guid? excludeId, CancellationToken cancellationToken)
    {
        string slug;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            slug = StatusPageSlug.Slugify(requested);
            if (!StatusPageSlug.IsValid(slug)) return null;
        }
        else
        {
            slug = StatusPageSlug.Slugify(title);
            if (!StatusPageSlug.IsValid(slug)) slug = StatusPageSlug.Random();
        }

        var taken = await db.StatusPages.AsNoTracking()
            .AnyAsync(p => p.Slug == slug && (excludeId == null || p.Id != excludeId), cancellationToken);
        return taken ? null : slug;
    }

    /// <summary>Validate that every selected service belongs to the current user; returns the
    /// owned id set, or null when any selection is foreign / missing.</summary>
    private async Task<HashSet<Guid>?> OwnedServiceIdsAsync(List<StatusPageItemUpsert>? items, CancellationToken cancellationToken)
    {
        var requested = (items ?? []).Select(i => i.WebResourceId).Distinct().ToList();
        if (requested.Count == 0) return [];

        var userId = UserId;
        var owned = await db.WebResources.AsNoTracking()
            .Where(s => s.UserId == userId && requested.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        return owned.Count == requested.Count ? owned.ToHashSet() : null;
    }

    /// <summary>Insert the selected items for a page straight onto the DbSet (never mutating the
    /// tracked navigation collection), mirroring the credentials/tags replace pattern. Dedupes
    /// and skips foreign services; <see cref="StatusPageItemEntity.SortOrder"/> follows request order.</summary>
    private void ApplyItems(Guid pageId, List<StatusPageItemUpsert>? items, HashSet<Guid> ownedServiceIds)
    {
        var order = 0;
        var seen = new HashSet<Guid>();
        foreach (var item in items ?? [])
        {
            if (!ownedServiceIds.Contains(item.WebResourceId) || !seen.Add(item.WebResourceId))
                continue;
            db.StatusPageItems.Add(new StatusPageItemEntity
            {
                StatusPageId = pageId,
                WebResourceId = item.WebResourceId,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? null : item.DisplayName.Trim(),
                SortOrder = order++,
            });
        }
    }

    private static StatusPageResponse Map(StatusPageEntity p) => new(
        p.Id, p.Title, p.Description, p.Slug, p.IsPublished, p.CreatedUtc, p.UpdatedUtc,
        p.Items
            .OrderBy(i => i.SortOrder)
            .Select(i => new StatusPageItemResponse(
                i.WebResourceId, i.WebResource?.Name ?? string.Empty, i.DisplayName, i.SortOrder))
            .ToList());
}
