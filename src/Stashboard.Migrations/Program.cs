using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;

// ─────────────────────────────────────────────────────────────────────────────
// Stashboard one-shot data-migration tool: copies a production PostgreSQL
// database into a fresh SQLite file with full fidelity (V4 migration).
//
//   dotnet run --project src/Stashboard.Migrations -- pg-to-sqlite \
//       --source "Host=...;Database=stashboard;Username=...;Password=..." \
//       --target "Data/app.db"
//
// The target SQLite file is created and migrated to the current schema, then
// every table is copied row-for-row preserving primary/foreign keys, users,
// audit rows and encrypted columns. Carry the SAME STASHBOARD_Encryption__Key
// over to the new deployment or the copied ciphertext cannot be decrypted.
// PostgreSQL is only read — it is never modified.
// ─────────────────────────────────────────────────────────────────────────────

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine(
        "Usage: pg-to-sqlite --source \"<postgres connection string>\" --target \"<sqlite file path or connection string>\"");
    return 1;
}

var (sourceConnection, targetConnection) = options.Value;

await using var source = new ApplicationDbContext(
    new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(sourceConnection).Options);

await using var target = new ApplicationDbContext(
    new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(targetConnection).Options);

Console.WriteLine("Applying schema to the target SQLite database...");
await target.Database.MigrateAsync();

Console.WriteLine("Copying data PostgreSQL -> SQLite...");
var report = await DatabaseCopier.CopyAllAsync(source, target);

var total = 0;
foreach (var (table, rows) in report)
{
    Console.WriteLine($"  {table,-22} {rows,8:n0} rows");
    total += rows;
}
Console.WriteLine($"Done. {total:n0} rows copied across {report.Count} tables.");
return 0;

static (string Source, string Target)? ParseArgs(string[] args)
{
    string? source = null;
    string? target = null;
    for (var i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--source": source = args[++i]; break;
            case "--target": target = args[++i]; break;
        }
    }

    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        return null;

    // Accept either a bare file path or a full SQLite connection string.
    var targetConnection = target.Contains('=', StringComparison.Ordinal)
        ? target
        : $"Data Source={target}";

    return (source, targetConnection);
}
