using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/categories")]
public class CategoriesController(ApplicationDbContext db, IStashboardMapper mapper) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<CategoryResponse>>> List(CancellationToken сancellationToken)
    {
        var userId = UserId;
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Name)
            .ToListAsync(сancellationToken);
        var counts = await db.WebResources.AsNoTracking()
            .Where(s => s.UserId == userId && s.CategoryId != null)
            .GroupBy(s => s.CategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, сancellationToken);
        return Ok(categories
            .Select(c => mapper.MapToCategoryResponse(c) with { ServiceCount = counts.GetValueOrDefault(c.Id) })
            .ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create([FromBody] CategoryUpsertRequest req, CancellationToken сancellationToken)
    {
        var entity = new CategoryEntity { UserId = UserId, Name = req.Name.Trim(), Color = req.Color };
        db.Categories.Add(entity);
        await db.SaveChangesAsync(сancellationToken);
        return CreatedAtAction(nameof(List), mapper.MapToCategoryResponse(entity));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CategoryResponse>> Update(Guid id, [FromBody] CategoryUpsertRequest req, CancellationToken сancellationToken)
    {
        var userId = UserId;
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, сancellationToken);
        if (entity is null) return NotFound();
        entity.Name = req.Name.Trim();
        entity.Color = req.Color;
        await db.SaveChangesAsync(сancellationToken);
        return Ok(mapper.MapToCategoryResponse(entity));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken сancellationToken)
    {
        var userId = UserId;
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, сancellationToken);
        if (entity is null) return NotFound();
        db.Categories.Remove(entity);
        await db.SaveChangesAsync(сancellationToken);
        return NoContent();
    }
}
