using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stashboard.Api.Services.Templates;

/// <summary>
/// V7.5 — loads the starter-recipe catalogue once at startup from the built-in
/// <c>templates/</c> folder plus an optional user override directory. Each
/// <c>*.json</c> file is structurally validated (id / project / service names,
/// at least one service, every service has an image); malformed files are logged
/// and skipped so one bad recipe never breaks the catalogue. A user template
/// replaces a built-in sharing its <c>id</c>.
/// </summary>
public sealed partial class ServiceTemplateCatalog : IServiceTemplateCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<ServiceTemplate> _templates;

    public ServiceTemplateCatalog(
        IWebHostEnvironment env, IConfiguration config, ILogger<ServiceTemplateCatalog> logger)
    {
        // The built-in catalogue ships next to the binaries (dev/tests) and in the
        // publish output (the image, where it equals the content root). Search both
        // so it is found regardless of how the app was launched.
        var contentRootTemplates = Path.Combine(env.ContentRootPath, "templates");
        var baseDirTemplates = Path.Combine(AppContext.BaseDirectory, "templates");
        var userDir = config["Templates:UserDirectory"];
        if (string.IsNullOrWhiteSpace(userDir))
            userDir = Path.Combine(env.ContentRootPath, "Data", "templates");

        // Built-in first, user dir last so a user recipe overrides a built-in
        // sharing its id.
        _templates = Load([contentRootTemplates, baseDirTemplates, userDir], logger);
    }

    public IReadOnlyList<ServiceTemplate> GetTemplates() => _templates;

    /// <summary>Reads + validates every <c>*.json</c> across the directories (in
    /// order; later dirs override earlier by id), returning the valid templates
    /// sorted by category then name. Exposed for tests.</summary>
    public static IReadOnlyList<ServiceTemplate> Load(IEnumerable<string> directories, ILogger logger)
    {
        var byId = new Dictionary<string, ServiceTemplate>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ServiceTemplate? template;
                try
                {
                    template = JsonSerializer.Deserialize<ServiceTemplate>(File.ReadAllText(file), JsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Skipping unreadable service template {File}", file);
                    continue;
                }

                if (Validate(template, out var error) && template is not null)
                {
                    byId[template.Id] = template;
                }
                else
                {
                    logger.LogWarning("Skipping invalid service template {File}: {Error}", file, error);
                }
            }
        }

        return byId.Values
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Validate(ServiceTemplate? t, out string error)
    {
        if (t is null) { error = "empty or null document"; return false; }
        if (string.IsNullOrWhiteSpace(t.Id) || !IdRegex().IsMatch(t.Id))
        { error = $"id '{t.Id}' must match ^[a-z0-9][a-z0-9-]*$"; return false; }
        if (string.IsNullOrWhiteSpace(t.Name)) { error = "name is required"; return false; }
        if (string.IsNullOrWhiteSpace(t.ProjectName) || !ProjectNameRegex().IsMatch(t.ProjectName))
        { error = $"projectName '{t.ProjectName}' must match ^[a-z0-9][a-z0-9_-]*$"; return false; }
        if (t.Services is null || t.Services.Count == 0)
        { error = "at least one service is required"; return false; }

        foreach (var s in t.Services)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || !ServiceNameRegex().IsMatch(s.Name))
            { error = $"service name '{s.Name}' must match ^[a-zA-Z0-9._-]+$"; return false; }
            if (string.IsNullOrWhiteSpace(s.Image))
            { error = $"service '{s.Name}' is missing an image"; return false; }
        }

        error = "";
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex ProjectNameRegex();

    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex ServiceNameRegex();
}
