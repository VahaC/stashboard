namespace Stashboard.Api.Contracts;

/// <summary>V10.6 — the full new global order of the user's service cards (Custom mode,
/// ungrouped). The list is the complete ordered set of ids; indices are rewritten 0..N.</summary>
public sealed record ServiceOrderRequest(List<Guid> OrderedServiceIds);

/// <summary>V10.6 — the new order of service cards within one category group (Custom mode,
/// grouped). <c>CategoryId</c> is null for the "Uncategorized" group.</summary>
public sealed record CategoryServiceOrderRequest(Guid? CategoryId, List<Guid> OrderedServiceIds);

/// <summary>V10.6 — the new order of the category groups themselves (Custom mode, grouped).</summary>
public sealed record CategoryOrderRequest(List<Guid> OrderedCategoryIds);
