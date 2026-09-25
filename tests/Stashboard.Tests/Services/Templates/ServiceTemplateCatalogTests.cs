using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Api.Services.Templates;

namespace Stashboard.Tests.Services.Templates;

/// <summary>V7.5 — the starter-recipe catalogue loader: reads + validates the
/// JSON files, skips malformed ones, sorts the result, and lets a user directory
/// override a built-in by id.</summary>
public sealed class ServiceTemplateCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-templates-" + Guid.NewGuid().ToString("N"));

    public ServiceTemplateCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteTemplate(string dir, string fileName, string json) =>
        File.WriteAllText(Path.Combine(dir, fileName), json);

    private const string ValidPostgres = """
        {
          "id": "postgres",
          "name": "PostgreSQL",
          "description": "Relational database.",
          "category": "Databases",
          "icon": "postgresql",
          "projectName": "postgres",
          "variables": [
            { "key": "PW", "label": "Password", "type": "password", "required": true, "generate": true }
          ],
          "services": [
            { "name": "db", "image": "postgres:17-alpine", "ports": ["5432:5432"], "volumes": [], "environment": [], "labels": [] }
          ]
        }
        """;

    [Fact]
    public void Load_ReadsValidTemplate()
    {
        var dir = Dir("builtin");
        WriteTemplate(dir, "postgres.json", ValidPostgres);

        var templates = ServiceTemplateCatalog.Load([dir], NullLogger.Instance);

        var t = Assert.Single(templates);
        Assert.Equal("postgres", t.Id);
        Assert.Equal("PostgreSQL", t.Name);
        Assert.Equal("db", Assert.Single(t.Services).Name);
        Assert.Equal("postgres:17-alpine", t.Services[0].Image);
        Assert.Equal("PW", Assert.Single(t.Variables).Key);
    }

    [Fact]
    public void Load_SkipsMalformedJson_WithoutThrowing()
    {
        var dir = Dir("builtin");
        WriteTemplate(dir, "postgres.json", ValidPostgres);
        WriteTemplate(dir, "broken.json", "{ not valid json");

        var templates = ServiceTemplateCatalog.Load([dir], NullLogger.Instance);

        Assert.Equal("postgres", Assert.Single(templates).Id);
    }

    [Theory]
    // bad id, bad project name, no services, service missing an image
    [InlineData("{ \"id\": \"Bad Id\", \"name\": \"x\", \"projectName\": \"x\", \"services\": [{ \"name\": \"s\", \"image\": \"i\" }] }")]
    [InlineData("{ \"id\": \"ok\", \"name\": \"x\", \"projectName\": \"Bad_Name!\", \"services\": [{ \"name\": \"s\", \"image\": \"i\" }] }")]
    [InlineData("{ \"id\": \"ok\", \"name\": \"x\", \"projectName\": \"ok\", \"services\": [] }")]
    [InlineData("{ \"id\": \"ok\", \"name\": \"x\", \"projectName\": \"ok\", \"services\": [{ \"name\": \"s\", \"image\": \"\" }] }")]
    public void Load_SkipsStructurallyInvalidTemplate(string json)
    {
        var dir = Dir("builtin");
        WriteTemplate(dir, "bad.json", json);

        Assert.Empty(ServiceTemplateCatalog.Load([dir], NullLogger.Instance));
    }

    [Fact]
    public void Load_UserDirectoryOverridesBuiltinById()
    {
        var builtin = Dir("builtin");
        var user = Dir("user");
        WriteTemplate(builtin, "postgres.json", ValidPostgres);
        WriteTemplate(user, "postgres.json",
            ValidPostgres.Replace("\"name\": \"PostgreSQL\"", "\"name\": \"My Postgres\""));

        var templates = ServiceTemplateCatalog.Load([builtin, user], NullLogger.Instance);

        Assert.Equal("My Postgres", Assert.Single(templates).Name);
    }

    [Fact]
    public void Load_SortsByCategoryThenName()
    {
        var dir = Dir("builtin");
        // Web-servers / Nginx written first, Databases / Redis second — discovery
        // order is alphabetical by file name, so the sort (not the read order) is
        // what must order the result.
        WriteTemplate(dir, "a-nginx.json", ValidPostgres
            .Replace("\"id\": \"postgres\"", "\"id\": \"nginx\"")
            .Replace("\"name\": \"PostgreSQL\"", "\"name\": \"Nginx\"")
            .Replace("\"category\": \"Databases\"", "\"category\": \"Web servers\""));
        WriteTemplate(dir, "b-redis.json", ValidPostgres
            .Replace("\"id\": \"postgres\"", "\"id\": \"redis\"")
            .Replace("\"name\": \"PostgreSQL\"", "\"name\": \"Redis\""));

        var templates = ServiceTemplateCatalog.Load([dir], NullLogger.Instance);

        Assert.Collection(templates,
            t => Assert.Equal("redis", t.Id),  // Databases / Redis
            t => Assert.Equal("nginx", t.Id)); // Web servers / Nginx
    }
}
