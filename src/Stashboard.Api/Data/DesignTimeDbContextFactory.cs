using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stashboard.Api.Data;

/// <summary>
/// Used by the EF Core CLI tools (<c>dotnet ef</c>) to construct an
/// <see cref="ApplicationDbContext"/> at design time. Migrations live in this
/// assembly, so no design-time database connection is needed — the provider
/// (SQLite) is all the tooling reads to emit migration SQL.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=Data/app.db")
            .Options;
        return new ApplicationDbContext(options);
    }
}
