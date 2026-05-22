using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Stashboard.Api.Data;

/// <summary>
/// Applies SQLite per-connection PRAGMAs on every connection open: WAL journal
/// mode (lets readers run concurrently with the single writer) and a busy
/// timeout so a transient write lock from the background scanners retries
/// instead of throwing "database is locked". Foreign-key enforcement is also
/// re-asserted (EF enables it by default, but it is cheap insurance).
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
