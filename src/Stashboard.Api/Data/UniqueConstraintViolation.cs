using Microsoft.EntityFrameworkCore;

namespace Stashboard.Api.Data;

/// <summary>
/// Detects unique-constraint violations from a <see cref="DbUpdateException"/>
/// in a provider-agnostic way. PostgreSQL surfaces the index name in the error
/// message (e.g. <c>IX_DockerWatches_DockerConnectionId_ContainerName</c>),
/// whereas SQLite surfaces the qualified columns
/// (<c>UNIQUE constraint failed: DockerWatches.DockerConnectionId, DockerWatches.ContainerName</c>).
/// Matching either form keeps duplicate detection working across both providers.
/// </summary>
internal static class UniqueConstraintViolation
{
    public static bool Matches(DbUpdateException ex, string indexName, params string[] qualifiedColumns)
    {
        var message = ex.InnerException?.Message ?? string.Empty;
        if (message.Contains(indexName, StringComparison.OrdinalIgnoreCase))
            return true;
        return qualifiedColumns.Length > 0
            && qualifiedColumns.All(c => message.Contains(c, StringComparison.OrdinalIgnoreCase));
    }
}
