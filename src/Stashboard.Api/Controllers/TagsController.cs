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
[Route("api/tags")]
public class TagsController(ApplicationDbContext db, IStashboardMapper mapper) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<TagResponse>>> List(CancellationToken сancellationToken)
    {
        var userId = UserId;
        var tags = await db.Tags.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Name)
            .ToListAsync(сancellationToken);
        var counts = await db.WebResourceTags.AsNoTracking()
            .Where(st => st.Tag.UserId == userId)
            .GroupBy(st => st.TagId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, сancellationToken);
        return Ok(tags
            .Select(t => mapper.MapToTagResponse(t) with { ServiceCount = counts.GetValueOrDefault(t.Id) })
            .ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TagResponse>> Create([FromBody] TagUpsertRequest req, CancellationToken сancellationToken)
    {
        var entity = new TagEntity { UserId = UserId, Name = req.Name.Trim() };
        db.Tags.Add(entity);
        await db.SaveChangesAsync(сancellationToken);
        return CreatedAtAction(nameof(List), mapper.MapToTagResponse(entity));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken сancellationToken)
    {
        var userId = UserId;
        var entity = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, сancellationToken);
        if (entity is null) return NotFound();
        db.Tags.Remove(entity);
        await db.SaveChangesAsync(сancellationToken);
        return NoContent();
    }
}
