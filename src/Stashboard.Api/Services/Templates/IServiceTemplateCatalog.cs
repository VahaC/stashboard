namespace Stashboard.Api.Services.Templates;

/// <summary>
/// V7.5 — the read-only catalogue of starter recipes behind the "From template"
/// tab. Templates are loaded once from the built-in <c>templates/</c> folder
/// shipped with the image and an optional user override directory (a mounted
/// volume for custom recipes — the opt-in extension point for the future
/// community-template source). Malformed files are skipped, never fatal.
/// </summary>
public interface IServiceTemplateCatalog
{
    /// <summary>All valid templates, sorted by category then name. A user
    /// template replaces a built-in sharing its <c>id</c>.</summary>
    IReadOnlyList<ServiceTemplate> GetTemplates();
}
