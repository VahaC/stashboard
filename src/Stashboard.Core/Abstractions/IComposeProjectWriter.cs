namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.1 — categorical outcome of an atomic Compose file save. Mirrors the
/// "never throw, branch on a typed status" contract of the V7.0 reader.
/// </summary>
public enum ComposeWriteStatus
{
    /// <summary>Validated and atomically renamed over the original.</summary>
    Ok = 0,

    /// <summary><c>docker compose config -q</c> rejected the edited file. The
    /// original is untouched; <see cref="ComposeWriteResult.Error"/> carries
    /// the CLI's stderr for the UI.</summary>
    ValidationFailed = 1,

    /// <summary>No usable Compose CLI to validate with — the save is refused
    /// (V7.1 decision: no validation, no write).</summary>
    CliNotAvailable = 2,

    /// <summary>The project directory is missing or the temp file could not be
    /// written / renamed.</summary>
    WriteFailed = 3,

    /// <summary>SSH transport failure (host unreachable, auth, timeout).</summary>
    SshFailed = 4,
}

/// <summary>V7.1 — result of one atomic save attempt.</summary>
public sealed record ComposeWriteResult(ComposeWriteStatus Status, string? Error)
{
    public bool IsSuccess => Status == ComposeWriteStatus.Ok;
    public static ComposeWriteResult Ok { get; } = new(ComposeWriteStatus.Ok, null);
}

/// <summary>
/// V7.1 — atomic, validated Compose file save. Both transports follow the same
/// sequence: write the new content to <c>&lt;file&gt;.next</c> in the project
/// directory, run <c>docker compose -f &lt;file&gt;.next config -q</c>, and only
/// on success rename it over the original (same-directory rename — atomic on
/// POSIX). Validation failure deletes the temp file and surfaces the raw
/// stderr; the original file is never touched until validation has passed.
/// </summary>
public interface IComposeProjectWriter
{
    /// <summary>Saves inside the Stashboard container (LocalSocket connections,
    /// V5.2 bind mount). Validates with the container's own Compose CLI.</summary>
    Task<ComposeWriteResult> WriteAsync(
        string projectPath, string fileName, string content, CancellationToken cancellationToken = default);

    /// <summary>V7.4.1 — like <see cref="WriteAsync(string,string,string,CancellationToken)"/>
    /// but for a brand-new project: when <paramref name="createDirectory"/> is
    /// set the project directory is created (<c>mkdir -p</c>) before the
    /// validated atomic write. Used by the "New project" flow.</summary>
    Task<ComposeWriteResult> WriteAsync(
        string projectPath, string fileName, string content, bool createDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Saves on the remote Docker host over the connection's existing
    /// SSH credentials (one round trip: upload + validate + rename). Validates
    /// with the host's Compose CLI. Never throws — SSH failures come back as
    /// <see cref="ComposeWriteStatus.SshFailed"/>.</summary>
    Task<ComposeWriteResult> WriteOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, string content,
        CancellationToken cancellationToken = default);

    /// <summary>V7.4.1 — SSH variant of the new-project write: when
    /// <paramref name="createDirectory"/> is set the remote directory is created
    /// (<c>mkdir -p</c>) before the validated atomic write.</summary>
    Task<ComposeWriteResult> WriteOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, string content,
        bool createDirectory, CancellationToken cancellationToken = default);
}
