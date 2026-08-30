using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.PersonalAccessTokens;
using Stashboard.Api.Data;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// A focused in-memory ASP.NET host that wires Stashboard's real authentication stack
/// (<see cref="StashboardAuthenticationExtensions.AddStashboardAuthentication"/>) over a SQLite
/// database and a few probe endpoints. It exercises the exact registration code the API uses, so
/// the JWT/PAT selector, scope policy, and deny filter are tested as they actually run — without
/// booting hosted services, static files, or migrations.
/// </summary>
public sealed class PatPipelineHost : IAsyncDisposable
{
    private static readonly JwtOptions Jwt = new() { Secret = "test-secret-test-secret-test-secret-test-secret" };

    private readonly IHost _host;
    private readonly string _dbPath;

    public HttpClient Client { get; }
    public UserEntity User { get; }

    private PatPipelineHost(IHost host, string dbPath, UserEntity user)
    {
        _host = host;
        _dbPath = dbPath;
        User = user;
        Client = host.GetTestClient();
    }

    public static async Task<PatPipelineHost> CreateAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pat-pipeline-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        // Build the schema once up front (no app migrations involved).
        using (var seed = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connectionString));
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(Options.Create(Jwt));
                    services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
                    services.AddScoped<ITokenService, TokenService>();
                    services.AddStashboardAuthentication(Jwt);
                    services.AddControllers().AddApplicationPart(typeof(PatPipelineTestController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapControllers());
                });
            })
            .Build();

        await host.StartAsync();

        var user = new UserEntity
        {
            Email = "owner@test.local",
            NormalizedEmail = "OWNER@TEST.LOCAL",
            PasswordHash = "x",
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return new PatPipelineHost(host, dbPath, user);
    }

    /// <summary>Mints a PAT for the seeded user and returns the plaintext secret.</summary>
    public async Task<string> CreatePatAsync(PersonalAccessTokenScope scope, DateTime? expiresUtc = null)
    {
        using var serviceScope = _host.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<IPersonalAccessTokenService>();
        var result = await service.CreateAsync(User.Id, "probe", scope, expiresUtc);
        return result.PlaintextSecret;
    }

    /// <summary>Issues a normal JWT access token for the seeded user (valid against the real clock).</summary>
    public async Task<string> IssueJwtAsync()
    {
        using var serviceScope = _host.Services.CreateScope();
        var tokens = serviceScope.ServiceProvider.GetRequiredService<ITokenService>();
        var pair = await tokens.IssueAsync(User);
        return pair.AccessToken;
    }

    /// <summary>Soft-revokes the token behind the given secret (mirrors what the management endpoint does).</summary>
    public async Task RevokeBySecretAsync(string plaintextSecret)
    {
        using var serviceScope = _host.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hint = "sb_pat_" + plaintextSecret.Substring(7, 4);
        await db.PersonalAccessTokens
            .Where(t => t.DisplayHint == hint)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, (DateTime?)DateTime.UtcNow));
    }

    public async Task<DateTime?> GetLastUsedAsync(string plaintextSecret)
    {
        using var serviceScope = _host.Services.CreateScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // The display hint encodes the prefix + first 4 chars of the secret — enough to find the one row.
        var hint = "sb_pat_" + plaintextSecret.Substring(7, 4);
        return await db.PersonalAccessTokens.AsNoTracking()
            .Where(t => t.DisplayHint == hint)
            .Select(t => t.LastUsedUtc)
            .FirstOrDefaultAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
