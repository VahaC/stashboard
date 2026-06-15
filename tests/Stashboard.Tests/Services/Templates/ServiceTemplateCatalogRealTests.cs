using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Api.Services.Templates;

namespace Stashboard.Tests.Services.Templates;

/// <summary>
/// V7.5 — sanity checks over the real shipped catalogue (the repo's
/// <c>templates/</c> folder), so a malformed, duplicate or mis-categorised recipe
/// fails CI rather than silently disappearing from the wizard at runtime.
/// </summary>
public sealed class ServiceTemplateCatalogRealTests
{
    /// <summary>Locates the repo's <c>templates/</c> folder by walking up from the
    /// test binaries until the solution file is found.</summary>
    private static string RepoTemplatesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Stashboard.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var templates = Path.Combine(dir!.FullName, "templates");
        Assert.True(Directory.Exists(templates), $"templates folder not found at {templates}");
        return templates;
    }

    [Fact]
    public void EveryShippedTemplateIsValid()
    {
        var dir = RepoTemplatesDir();
        var fileCount = Directory.EnumerateFiles(dir, "*.json").Count();

        var templates = ServiceTemplateCatalog.Load([dir], NullLogger.Instance);

        // A skipped (invalid) file would make the loaded count fall short.
        Assert.Equal(fileCount, templates.Count);
        Assert.True(templates.Count >= 100, $"expected a sizeable catalogue, got {templates.Count}");
    }

    [Fact]
    public void TemplateIdsAreUnique()
    {
        var templates = ServiceTemplateCatalog.Load([RepoTemplatesDir()], NullLogger.Instance);
        var ids = templates.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryCategoryHasAtLeastTenTemplates()
    {
        var templates = ServiceTemplateCatalog.Load([RepoTemplatesDir()], NullLogger.Instance);

        var thin = templates
            .GroupBy(t => t.Category)
            .Where(g => g.Count() < 10)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        Assert.True(thin.Count == 0, "categories with fewer than 10 templates: " + string.Join(", ", thin));
    }

    [Fact]
    public void EveryServiceVolumeAndPortPlaceholderHasABackingVariable()
    {
        // A typo'd ${KEY} that no variable defines would silently resolve to an
        // empty string on the client — catch it here.
        var templates = ServiceTemplateCatalog.Load([RepoTemplatesDir()], NullLogger.Instance);

        foreach (var t in templates)
        {
            var keys = t.Variables.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var s in t.Services)
            {
                var strings = s.Ports
                    .Concat(s.Volumes)
                    .Concat(s.Environment.Select(e => e.Value ?? ""))
                    .Concat([s.Command ?? "", s.Entrypoint ?? "", s.WorkingDir ?? "", s.User ?? ""]);
                foreach (var str in strings)
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(str, @"\$\{([A-Za-z0-9_]+)\}"))
                        Assert.True(keys.Contains(m.Groups[1].Value),
                            $"template '{t.Id}' references ${{{m.Groups[1].Value}}} with no matching variable");
            }
        }
    }
}
