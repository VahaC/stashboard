namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.1 — the desired final state of one service's editable fields. The editor
/// diffs this against the file's current (normalised) state and only touches
/// the keys whose value actually changed, so an untouched field is a guaranteed
/// zero-diff. Scalar <c>null</c> / empty list means "remove the key".
/// </summary>
/// <remarks>List shapes use the same normalised short syntax the V7.0 viewer
/// returns (<c>published:target/protocol</c> ports, <c>source:target[:ro]</c>
/// volumes), so the UI can send back exactly what <c>GET /compose</c> gave it.
/// <see cref="Command"/> / <see cref="Entrypoint"/> accept either a plain
/// string (shell form) or a <c>["exec", "form"]</c> flow list.</remarks>
public sealed record ComposeServiceEdit(
    string? Image,
    string? Restart,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ComposeEnvVar> Environment,
    IReadOnlyList<ComposeEnvVar> Labels,
    string? Command,
    string? Entrypoint,
    string? User,
    string? WorkingDir,
    /// <summary>V7.2 — the desired resource constraints. <see cref="ComposeResourceConstraints.Convention"/>
    /// tells the editor whether cpu/mem/pids go into <c>deploy.resources</c> or
    /// the legacy top-level keys; the editor only ever writes the chosen
    /// convention, never mixing the two in one file.</summary>
    ComposeResourceConstraints Resources);

/// <summary>
/// V7.1 — result of <see cref="IComposeFileEditor.ApplyServiceEdit"/>. Exactly
/// one of <see cref="Content"/> / <see cref="Error"/> is set. <see cref="Changed"/>
/// is <c>false</c> when the desired state already matches the file (the caller
/// then skips the write entirely).
/// </summary>
public sealed record ComposeEditResult(
    string? Content,
    bool Changed,
    string? Error)
{
    public static ComposeEditResult Unchanged(string content) => new(content, false, null);
    public static ComposeEditResult Edited(string content) => new(content, true, null);
    public static ComposeEditResult Failed(string error) => new(null, false, error);
}

/// <summary>
/// V7.1 — pure YAML-text → YAML-text service editor. Applies a
/// <see cref="ComposeServiceEdit"/> to one service by splicing the raw file
/// text at the exact spans of the edited keys (located via the YAML event
/// stream), so comments, key order, quoting and formatting everywhere else in
/// the file survive byte-for-byte — the round-trip fidelity bar of the V7
/// editor line. No file or Docker access.
/// </summary>
public interface IComposeFileEditor
{
    /// <summary>Returns the edited YAML text, or a typed error when the service
    /// is missing or the construct can't be edited safely (flow-style service
    /// body, anchored values, merge keys). Never throws on bad input.</summary>
    ComposeEditResult ApplyServiceEdit(string yamlText, string serviceName, ComposeServiceEdit edit);

    /// <summary>
    /// V7.4 — appends a brand-new service to the file's <c>services:</c> map,
    /// rendered from <paramref name="edit"/> (the same desired-state shape the
    /// service editor uses). The new entry lands after the last existing
    /// service at the siblings' indentation column, so comments / key order /
    /// formatting everywhere else survive byte-for-byte. Creates the whole
    /// <c>services:</c> section when it is somehow absent. Returns a typed error
    /// when the name is malformed (must match <c>^[a-zA-Z0-9._-]+$</c>), already
    /// taken, the image is missing, or the <c>services:</c> section uses a
    /// construct that can't be extended safely (flow style, anchors). Never
    /// throws on bad input.
    /// </summary>
    ComposeEditResult AddService(string yamlText, string serviceName, ComposeServiceEdit edit);

    /// <summary>
    /// V7.4.1 — builds a brand-new Compose file from scratch: a top-level
    /// <c>name:</c> (so the project name is deterministic regardless of the
    /// directory) plus a <c>services:</c> map holding one service rendered from
    /// <paramref name="edit"/>. Returns a typed error when the project name is
    /// malformed (must match <c>^[a-z0-9][a-z0-9_-]*$</c> — Compose's own
    /// lowercase rule), the service name is malformed
    /// (<c>^[a-zA-Z0-9._-]+$</c>), or the image is missing. The result is plain
    /// text; the caller writes + validates it like any other save.
    /// </summary>
    ComposeEditResult CreateFile(string projectName, string serviceName, ComposeServiceEdit edit);

    /// <summary>
    /// V7.3 — adds or replaces one top-level resource entry (a network / volume /
    /// secret / config) by rewriting just that entry's lines, the same
    /// comment-preserving, per-entry granularity the service editor uses. Creates
    /// the whole top-level section (<c>networks:</c> …) when it is absent. Returns
    /// <see cref="ComposeEditResult.Unchanged"/> when the desired state already
    /// matches the file, or a typed error when the section / entry uses a
    /// construct that can't be spliced safely (flow style, anchors).
    /// </summary>
    ComposeEditResult ApplyResourceEdit(string yamlText, ComposeResourceEdit edit);

    /// <summary>
    /// V7.3 — removes one top-level resource entry. When it was the section's only
    /// entry the whole section key is removed too. Unchanged when the entry was
    /// already absent.
    /// </summary>
    ComposeEditResult RemoveResource(string yamlText, ComposeResourceKind kind, string name);
}

/// <summary>V7.3 — which top-level Compose section a <see cref="ComposeResourceEdit"/>
/// targets.</summary>
public enum ComposeResourceKind
{
    Network = 0,
    Volume = 1,
    Secret = 2,
    Config = 3,
}

/// <summary>
/// V7.3 — desired final state of one top-level resource entry. The shape is a
/// superset across the four kinds; the editor renders only the fields that apply
/// to <see cref="Kind"/> (e.g. <see cref="Subnet"/> / <see cref="Gateway"/> are
/// networks-only, <see cref="File"/> is secrets/configs-only). When
/// <see cref="External"/> is set, only <c>external: true</c> (+ optional
/// <see cref="NameOverride"/>) is written and the driver/ipam/file fields are
/// ignored.
/// </summary>
public sealed record ComposeResourceEdit(
    ComposeResourceKind Kind,
    string Name,
    bool External,
    string? NameOverride,
    string? Driver,
    string? Subnet,
    string? Gateway,
    string? File,
    IReadOnlyList<ComposeEnvVar> DriverOpts);
