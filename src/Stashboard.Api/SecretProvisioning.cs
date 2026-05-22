using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Stashboard.Infrastructure.Security;

namespace Stashboard.Api;

/// <summary>
/// First-run secret provisioning. When the operator has not supplied an
/// encryption key or JWT signing secret (env var / appsettings), strong random
/// values are generated once and persisted next to the SQLite database so they
/// survive image updates and container recreation. Explicit configuration always
/// wins and disables persistence for that secret — so existing deployments,
/// external secret stores, and key rotation keep working unchanged.
/// </summary>
internal static class SecretProvisioning
{
    /// <summary>
    /// Fills any missing <c>Encryption:Key</c> / <c>Jwt:Secret</c> from a
    /// persisted-secrets directory and feeds them back into configuration via an
    /// in-memory source (added only for the missing keys, so it never shadows an
    /// explicit value). Returns human-readable notes for the startup log.
    /// </summary>
    public static IReadOnlyList<string> AddPersistedSecrets(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var directory = ResolveSecretsDirectory(config, builder.Environment.ContentRootPath);

        var overrides = new Dictionary<string, string?>();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(config["Encryption:Key"]))
        {
            var (value, created) = PersistedSecretProvider.GetOrCreate(
                directory, "encryption.key", AesEncryptionService.GenerateKey);
            overrides["Encryption:Key"] = value;
            notes.Add(created
                ? $"Generated a new encryption key and stored it under '{directory}'. Back this directory up — losing it makes every stored credential undecryptable."
                : "Loaded the persisted encryption key.");
        }

        if (string.IsNullOrWhiteSpace(config["Jwt:Secret"]))
        {
            var (value, created) = PersistedSecretProvider.GetOrCreate(
                directory, "jwt.secret", () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            overrides["Jwt:Secret"] = value;
            notes.Add(created
                ? "Generated a new JWT signing secret and persisted it."
                : "Loaded the persisted JWT signing secret.");
        }

        if (overrides.Count > 0)
            builder.Configuration.AddInMemoryCollection(overrides);

        return notes;
    }

    /// <summary>
    /// Where persisted secrets live. An explicit <c>Stashboard:SecretsPath</c> wins;
    /// otherwise a <c>.secrets</c> folder beside the SQLite database, which already
    /// sits on the volume that survives updates. Falls back to the content root if
    /// the connection string can't be parsed (e.g. an in-memory test database).
    /// </summary>
    private static string ResolveSecretsDirectory(IConfiguration config, string contentRoot)
    {
        var configured = config["Stashboard:SecretsPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var dbDirectory = TryGetSqliteDirectory(config.GetConnectionString("DefaultConnection"));
        var baseDirectory = !string.IsNullOrWhiteSpace(dbDirectory) ? dbDirectory : contentRoot;
        return Path.Combine(baseDirectory, ".secrets");
    }

    private static string? TryGetSqliteDirectory(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;
        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
                return null;
            return Path.GetDirectoryName(Path.GetFullPath(dataSource));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
