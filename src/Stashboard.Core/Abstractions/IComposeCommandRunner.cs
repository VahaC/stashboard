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
}
