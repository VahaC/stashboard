using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.6 — persists the dashboard's Custom drag-and-drop order. There are two
/// independent card orders (a global one used when ungrouped, and a per-category
/// one used when grouped) plus a group order, so toggling category grouping never
/// scrambles the other mode's arrangement. Each endpoint receives the complete
/// ordered id list for its slice and rewrites dense 0..N indices in one transaction;
/// ids the user doesn't own are ignored.
/// </summary>
[ApiController]
[Authorize]
[Route("api/dashboard")]
public class DashboardController(ApplicationDbContext db) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    /// <summary>Custom order of every service card (ungrouped mode).</summary>
    [HttpPut("service-order")]
    public async Task<IActionResult> SetServiceOrder([FromBody] ServiceOrderRequest request, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var services = await db.WebResources.Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        ApplyOrder(request.OrderedServiceIds, services, s => s.Id, (s, i) => s.SortOrder = i);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Custom order of the cards inside one category group (grouped mode).</summary>
    [HttpPut("service-order-in-category")]
    public async Task<IActionResult> SetServiceOrderInCategory(
        [FromBody] CategoryServiceOrderRequest request, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var services = await db.WebResources
            .Where(s => s.UserId == userId && s.CategoryId == request.CategoryId)
            .ToListAsync(cancellationToken);
        ApplyOrder(request.OrderedServiceIds, services, s => s.Id, (s, i) => s.SortOrderInCategory = i);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Custom order of the category groups (grouped mode).</summary>
    [HttpPut("category-order")]
    public async Task<IActionResult> SetCategoryOrder([FromBody] CategoryOrderRequest request, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var categories = await db.Categories.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        ApplyOrder(request.OrderedCategoryIds, categories, c => c.Id, (c, i) => c.SortOrder = i);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Rewrites a dense 0..N index onto each owned row that appears in
    /// <paramref name="orderedIds"/>, in that order. Unknown ids are ignored.</summary>
    private static void ApplyOrder<T>(
        IReadOnlyList<Guid> orderedIds, IEnumerable<T> rows, Func<T, Guid> idOf, Action<T, int> setIndex)
    {
        var position = new Dictionary<Guid, int>(orderedIds.Count);
        for (var i = 0; i < orderedIds.Count; i++)
            position[orderedIds[i]] = i;

        foreach (var row in rows)
            if (position.TryGetValue(idOf(row), out var index))
                setIndex(row, index);
    }
}
