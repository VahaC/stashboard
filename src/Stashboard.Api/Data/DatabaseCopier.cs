using Microsoft.EntityFrameworkCore;

namespace Stashboard.Api.Data;

/// <summary>
/// One-shot, full-fidelity copy of every row from one
/// <see cref="ApplicationDbContext"/> to another. Provider-agnostic: the caller
/// chooses the providers (the V4 migration wires source = PostgreSQL,
/// target = SQLite). Primary keys, foreign keys, timestamps, users, refresh
/// tokens, audit rows and already-encrypted columns are all copied verbatim —
/// nothing is re-generated or re-encrypted — so the destination is an exact
/// replica. The same encryption key must therefore be carried over to the new
/// deployment for the ciphertext to remain decryptable.
/// </summary>
public static class DatabaseCopier
{
    public sealed record TableResult(string Table, int Rows);

    /// <summary>
    /// Copies all tables from <paramref name="source"/> into
    /// <paramref name="target"/> in foreign-key-safe order. The target must
    /// already have its schema (run migrations first) and be empty.
    /// </summary>
    public static async Task<IReadOnlyList<TableResult>> CopyAllAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken = default)
    {
        await EnsureTargetEmptyAsync(target, cancellationToken);

        var results = new List<TableResult>
        {
            // Parents before children so foreign keys resolve on insert.
            await CopyAsync(source.Users, target, cancellationToken),
            await CopyAsync(source.Categories, target, cancellationToken),
            await CopyAsync(source.Tags, target, cancellationToken),
            await CopyAsync(source.DockerConnections, target, cancellationToken),
            await CopyAsync(source.WebResources, target, cancellationToken),
            await CopyAsync(source.Credentials, target, cancellationToken),
            await CopyAsync(source.WebResourceTags, target, cancellationToken),
            await CopyAsync(source.DockerWatches, target, cancellationToken),
            await CopyAsync(source.DockerUpdateAttempts, target, cancellationToken),
            await CopyAsync(source.ComposeChangeAudits, target, cancellationToken),
            await CopyAsync(source.RefreshTokens, target, cancellationToken),
        };

        return results;
    }

    private static async Task<TableResult> CopyAsync<T>(
        DbSet<T> sourceSet,
        ApplicationDbContext target,
        CancellationToken cancellationToken) where T : class
    {
        var rows = await sourceSet.AsNoTracking().ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            await target.Set<T>().AddRangeAsync(rows, cancellationToken);
            await target.SaveChangesAsync(cancellationToken);
            // Detach so the tracker doesn't accumulate across tables and so the
            // next table's flat rows can't collide on identity resolution.
            target.ChangeTracker.Clear();
        }
        return new TableResult(typeof(T).Name, rows.Count);
    }

    private static async Task EnsureTargetEmptyAsync(ApplicationDbContext target, CancellationToken ct)
    {
        var populated =
            await target.Users.AnyAsync(ct)
            || await target.WebResources.AnyAsync(ct)
            || await target.DockerConnections.AnyAsync(ct)
            || await target.DockerWatches.AnyAsync(ct);

        if (populated)
            throw new InvalidOperationException(
                "Target database is not empty. Copy aborted to avoid clobbering existing data.");
    }
}
