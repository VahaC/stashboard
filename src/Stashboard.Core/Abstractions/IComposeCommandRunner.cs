namespace Stashboard.Core.Abstractions;

/// <summary>
/// V5.2 — categorical outcome of a <c>docker compose</c> shell-out. Mirrors the
/// "never throw, branch on a typed status" contract the rest of the Docker
/// surface uses so <see cref="IDockerImageUpdater"/> can map the result onto a
/// <see cref="Stashboard.Core.Enums.DockerUpdateAttemptStatus"/> without
/// interpreting process exit codes itself.
/// </summary>
public enum ComposeRunnerStatus
{
    /// <summary>The <c>pull</c> + <c>up -d</c> sequence completed with exit code 0.</summary>
    Success = 0,

    /// <summary>Neither the <c>docker compose</c> plugin nor the standalone
    /// <c>docker-compose</c> binary is present. Callers fall back to the raw
    /// <c>Docker.DotNet</c> recreate.</summary>
    CliNotAvailable = 1,

    /// <summary>The configured Compose project directory does not exist inside
    /// the Stashboard container — almost always a missing / wrong bind mount.</summary>
    ProjectPathNotFound = 2,

    /// <summary>The CLI ran but exited non-zero (pull or recreate failed).</summary>
    CommandFailed = 3,
}

/// <summary>
/// V5.2 — what the updater needs to recreate one Compose-managed service.
/// </summary>
/// <param name="ProjectPath">Absolute path, inside the Stashboard container, to
/// the directory that holds the Compose file (the bind-mount target of the
/// host's project directory).</param>
/// <param name="ServiceName">The Compose service to recreate — the value of the
/// running container's <c>com.docker.compose.service</c> label.</param>
public sealed record ComposeRecreateRequest(string ProjectPath, string ServiceName);

/// <summary>
/// V5.4 — what the updater needs to bulk-update every service in a Compose
/// project. The whole project is pulled then brought back up in one shot so
/// Compose itself honours <c>depends_on</c> ordering across the stack.
/// </summary>
/// <param name="ProjectPath">Same in-container path as for the per-service
/// recreate.</param>
public sealed record ComposeProjectRecreateRequest(string ProjectPath);

/// <summary>
/// V7.4 — what the "Save and run" CTA needs to bring a Compose project up.
/// Runs <c>docker compose up -d</c> (no <c>pull</c>) against the whole project
/// so a freshly-added service is created/started without disturbing the
/// already-running siblings. Unlike <see cref="ComposeProjectRecreateRequest"/>
/// this path also serves SSH hosts: when <see cref="Ssh"/> is set the command
/// runs over the connection's existing SSH credentials on the remote host;
/// when it is <c>null</c> the local <c>docker compose</c> CLI inside the
/// Stashboard container is used (LocalSocket connections).
/// </summary>
public sealed record ComposeUpRequest(string ProjectPath, DockerSshCredentials? Ssh);

/// <summary>
/// V5.2 — result of a <see cref="IComposeCommandRunner.RecreateServiceAsync"/>
/// call. <see cref="Output"/> / <see cref="Error"/> carry the captured stdout /
/// stderr so a failure can surface an actionable message on the audit row.
/// </summary>
public sealed record ComposeRunResult(
    ComposeRunnerStatus Status,
    int? ExitCode,
    string? Output,
    string? Error)
{
    public bool IsSuccess => Status == ComposeRunnerStatus.Success;
}

/// <summary>
/// V5.2 — shells out to the host's <c>docker compose</c> CLI to perform a
/// true Compose-aware recreate (so <c>env_file</c> resolution,
/// <c>depends_on</c> ordering, profiles and Compose's own network / subnet
/// allocation are honoured — none of which the raw <c>Docker.DotNet</c> recreate
/// can replicate). Local-socket only; remote (TCP / SSH) connections stay on
/// the raw path. Never throws — every failure mode is a typed
/// <see cref="ComposeRunnerStatus"/>.
/// </summary>
public interface IComposeCommandRunner
{
    /// <summary>
    /// Whether a usable Compose CLI (the <c>docker compose</c> plugin or the
    /// standalone <c>docker-compose</c> binary) is present in this environment.
    /// Result is cached after the first probe.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <c>docker compose pull &lt;service&gt;</c> then
    /// <c>docker compose up -d &lt;service&gt;</c> against the project at
    /// <see cref="ComposeRecreateRequest.ProjectPath"/>. The <c>up -d</c> step
    /// (without <c>--no-deps</c>) lets Compose restart dependencies in
    /// <c>depends_on</c> order.
    /// </summary>
    Task<ComposeRunResult> RecreateServiceAsync(
        ComposeRecreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// V5.4 — runs <c>docker compose pull</c> then <c>docker compose up -d</c>
    /// against the whole project at <see cref="ComposeProjectRecreateRequest.ProjectPath"/>.
    /// No service argument is passed so Compose pulls and recreates every
    /// service in the file in the correct <c>depends_on</c> order. Used by
    /// the V5.4 bulk "Update project" button.
    /// </summary>
    Task<ComposeRunResult> RecreateProjectAsync(
        ComposeProjectRecreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.4 — runs <c>docker compose up -d</c> against the whole project at
    /// <see cref="ComposeUpRequest.ProjectPath"/>. No <c>pull</c> and no service
    /// argument: Compose creates/starts whatever is missing or changed (the
    /// newly-added service) and leaves unchanged containers running, in
    /// <c>depends_on</c> order. Local (<see cref="ComposeUpRequest.Ssh"/>
    /// <c>null</c>) or over SSH on the remote host. Never throws — every failure
    /// mode is a typed <see cref="ComposeRunnerStatus"/>.
    /// </summary>
    Task<ComposeRunResult> UpProjectAsync(
        ComposeUpRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.1 — runs <c>docker compose -f &lt;composeFilePath&gt; config -q</c>
    /// with <paramref name="projectPath"/> as the working directory, so a
    /// candidate file (the editor's <c>.next</c> temp file) is validated by
    /// Compose itself — including <c>env_file</c> resolution — before the
    /// writer renames it over the original. A non-zero exit comes back as
    /// <see cref="ComposeRunnerStatus.CommandFailed"/> with the raw stderr.
    /// </summary>
    Task<ComposeRunResult> ValidateConfigAsync(
        string projectPath, string composeFilePath, CancellationToken cancellationToken = default);
}
