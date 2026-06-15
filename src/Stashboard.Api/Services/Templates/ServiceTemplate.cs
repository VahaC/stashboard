using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Templates;

/// <summary>
/// V7.5 — one starter recipe shipped with the image (a <c>templates/*.json</c>
/// file). A template is the structured, pre-filled shape behind the "From
/// template" tab of the V7.4 new-project wizard: enough metadata to render a
/// card (<see cref="Name"/> / <see cref="Description"/> / <see cref="Icon"/> /
/// <see cref="Category"/>), the <see cref="Variables"/> the user fills per
/// deployment (volume host paths, env values, exposed ports), and the
/// <see cref="Services"/> the project is built from. The frontend substitutes
/// each <c>${KEY}</c> placeholder with the user's variable value and posts the
/// resolved services to the same <c>create-project</c> endpoint the from-scratch
/// flow uses — the backend stays template-agnostic.
/// </summary>
public sealed record ServiceTemplate(
    /// <summary>Stable slug, unique across the catalogue (<c>^[a-z0-9][a-z0-9-]*$</c>).
    /// A user template in the override directory replaces a built-in with the same id.</summary>
    string Id,
    string Name,
    string Description,
    /// <summary>Grouping label for the picker (e.g. "Databases", "Networking").</summary>
    string Category,
    /// <summary>dashboard-icons slug (e.g. <c>postgresql</c>); the frontend builds the
    /// CDN URL and falls back to a letter badge when the logo fails to load.</summary>
    string? Icon,
    /// <summary>Default Compose project name; the user can edit it before creating.</summary>
    string ProjectName,
    IReadOnlyList<ServiceTemplateVariable> Variables,
    /// <summary>The services the project is built from. Field strings may carry
    /// <c>${KEY}</c> placeholders resolved from <see cref="Variables"/> on the
    /// client. The shape mirrors <see cref="ComposeServiceCreateRequest"/> so the
    /// resolved value posts straight to <c>create-project</c>.</summary>
    IReadOnlyList<ComposeServiceCreateRequest> Services,
    /// <summary>Optional note shown after creation (e.g. default credentials).</summary>
    string? Notes,
    /// <summary>Optional documentation URL.</summary>
    string? Documentation);

/// <summary>
/// V7.5 — one per-deployment input the user fills before the project is created.
/// The placeholder <c>${Key}</c> wherever it appears in the template's service
/// fields is replaced with the entered value (or <see cref="Default"/>).
/// </summary>
public sealed record ServiceTemplateVariable(
    /// <summary>Placeholder name without the <c>${…}</c> wrapper (e.g. <c>DATA_PATH</c>).</summary>
    string Key,
    string Label,
    /// <summary>Input flavour: <c>text</c> (default), <c>password</c> (offers a
    /// generate button + masked input), <c>path</c>, or <c>port</c>.</summary>
    string? Type,
    string? Hint,
    string? Default,
    /// <summary>When true the user must supply a non-empty value before "Create".</summary>
    bool Required,
    /// <summary>When true a "generate" affordance fills a random secret (passwords).</summary>
    bool Generate);
