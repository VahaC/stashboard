namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.7 — severity of one <see cref="ComposeLintFinding"/>. An
/// <see cref="Error"/> is a problem <c>docker compose up</c> will reject or that
/// breaks the project (port clash, dependency cycle, a <c>service_healthy</c>
/// condition on a service with no healthcheck); a <see cref="Warning"/> is a
/// smell worth surfacing but not blocking (deprecated keys, a bind mount that
/// escapes the project root, an image pinned to <c>latest</c>).
/// </summary>
public enum ComposeLintSeverity
{
    Warning = 0,
    Error = 1,
}

/// <summary>
/// V7.7 — one finding from the Compose linter. <see cref="Rule"/> is a stable
/// kebab-case id (so the UI can group / suppress without parsing
/// <see cref="Message"/>); <see cref="Service"/> names the service the finding
/// renders on (<c>null</c> for a project-level finding, e.g. a top-level
/// <c>version:</c> key). A single problem spanning several services (a port
/// clash, a cycle) is emitted once per involved service so each card shows it.
/// </summary>
public sealed record ComposeLintFinding(
    string Rule,
    ComposeLintSeverity Severity,
    string Message,
    string? Service);

/// <summary>
/// V7.7 — pure Compose linter. Runs the V7.7 rule set over the raw Compose YAML
/// text and returns the findings the editor renders inline on each service card
/// and aggregates into the project-level Health badge. Operates on the raw text
/// (not the parsed <see cref="ComposeProjectModel"/>) so it can see the
/// constructs the viewer model intentionally drops — <c>depends_on</c>
/// conditions, <c>healthcheck</c> presence and the deprecated <c>links</c> /
/// <c>volumes_from</c> / top-level <c>version:</c> keys. No file or Docker
/// access; the seam keeps it trivially unit-testable.
/// </summary>
public interface IComposeLinter
{
    /// <summary>Lints <paramref name="yamlText"/>. <paramref name="projectPath"/>
    /// is the project directory (the bind-mount root) used by the bind-mount
    /// escape rule; pass <c>null</c> when unknown (that rule then only flags
    /// clearly-relative <c>..</c> escapes). Never throws — a file that fails to
    /// parse yields no findings (the parser already surfaces the parse error).</summary>
    IReadOnlyList<ComposeLintFinding> Lint(string yamlText, string? projectPath = null);
}
