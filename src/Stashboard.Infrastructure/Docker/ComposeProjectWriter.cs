using System.Text;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.1 — production <see cref="IComposeProjectWriter"/>. Atomic, validated
/// save in both transports: write <c>&lt;file&gt;.next</c>, run
/// <c>docker compose -f &lt;file&gt;.next config -q</c>, and only on success
/// rename it over the original (same-directory rename — atomic on POSIX).
/// Validation is blocking by design (V7.1 decision): no Compose CLI means no
/// save, so an editor bug can never persist a file Compose itself rejects.
/// </summary>
/// <remarks>The filesystem operations and the SSH command execution are
/// exposed as settable seams (same pattern as <see cref="ComposeProjectReader"/>)
/// so unit tests can drive every branch without touching a real filesystem or
/// opening a real SSH session.</remarks>
public sealed class ComposeProjectWriter(
    IComposeCommandRunner composeCommandRunner,
    IComposeHistoryStore historyStore) : IComposeProjectWriter
{
    /// <summary>Remote-script exit codes for the typed failure states (outside
    /// the 0–2 shell-builtin range; mirrors the reader's probe convention).</summary>
    private const int ExitDirectoryNotFound = 40;
    private const int ExitCliNotAvailable = 42;
    private const int ExitWriteFailed = 43;
    private const int ExitValidationFailed = 44;
    private const int ExitRenameFailed = 45;

    /// <summary>Test seam — writes the candidate file.</summary>
    public Func<string, string, CancellationToken, Task> WriteFileAsync { get; set; }
        = (path, content, ct) => File.WriteAllTextAsync(path, content, ct);

    /// <summary>Test seam — atomically renames the candidate over the original.</summary>
    public Action<string, string> MoveFile { get; set; }
        = (from, to) => File.Move(from, to, overwrite: true);

    /// <summary>Test seam — best-effort cleanup of the candidate file.</summary>
    public Action<string> DeleteFile { get; set; } = path => File.Delete(path);

    /// <summary>Test seam — checks whether the project directory exists.</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    /// <summary>V7.4.1 — test seam — creates the project directory (and parents)
    /// for the new-project write.</summary>
    public Action<string> CreateDirectory { get; set; } = path => Directory.CreateDirectory(path);

    /// <summary>Test seam — runs one command over SSH. Production delegates to
    /// the shared <see cref="SshCommandExecutor"/>.</summary>
    public Func<DockerSshCredentials, string, CancellationToken, Task<SshCommandOutcome>> RunSshCommandAsync { get; set; }
        = SshCommandExecutor.RunAsync;

    public async Task<ComposeWriteResult> WriteAsync(
        string projectPath, string fileName, string content, bool createDirectory,
        CancellationToken cancellationToken = default)
    {
        if (createDirectory)
        {
            try { CreateDirectory(projectPath); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                    $"Creating directory '{projectPath}' failed: {ex.Message}");
            }
        }
        return await WriteAsync(projectPath, fileName, content, cancellationToken);
    }

    public async Task<ComposeWriteResult> WriteAsync(
        string projectPath, string fileName, string content, CancellationToken cancellationToken = default)
    {
        if (!DirectoryExists(projectPath))
            return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Compose project directory '{projectPath}' was not found inside the container.");

        // V7.6 — snapshot the current file into the project's history directory
        // before we overwrite it, so the Restore button can roll the save back.
        // Best-effort: a history failure must never block the actual save.
        await historyStore.SnapshotAsync(projectPath, fileName, cancellationToken);

        var targetPath = Path.Combine(projectPath, fileName);
        var nextPath = targetPath + ".next";

        try
        {
            await WriteFileAsync(nextPath, content, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Writing '{nextPath}' failed: {ex.Message}");
        }

        var validation = await composeCommandRunner.ValidateConfigAsync(projectPath, nextPath, cancellationToken);
        if (!validation.IsSuccess)
        {
            TryDelete(nextPath);
            return validation.Status == ComposeRunnerStatus.CliNotAvailable
                ? new ComposeWriteResult(ComposeWriteStatus.CliNotAvailable,
                    "Cannot validate the edited file: the docker compose CLI is not available inside the Stashboard container. The save was refused.")
                : new ComposeWriteResult(ComposeWriteStatus.ValidationFailed,
                    validation.Error ?? "docker compose config rejected the edited file.");
        }

        try
        {
            MoveFile(nextPath, targetPath);
        }
        catch (Exception ex)
        {
            TryDelete(nextPath);
            return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Replacing '{targetPath}' failed: {ex.Message}");
        }

        return ComposeWriteResult.Ok;
    }

    public Task<ComposeWriteResult> WriteOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, string content,
        CancellationToken cancellationToken = default) =>
        WriteOverSshAsync(ssh, projectPath, fileName, content, createDirectory: false, cancellationToken);

    public async Task<ComposeWriteResult> WriteOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, string content,
        bool createDirectory, CancellationToken cancellationToken = default)
    {
        // V7.6 — snapshot the current remote file into history before overwriting
        // it (best-effort, never blocks the save). Runs as its own short round
        // trip so the write script below stays the proven V7.1 sequence.
        await historyStore.SnapshotOverSshAsync(ssh, projectPath, fileName, cancellationToken);

        // One round trip: upload the candidate (base64 survives any shell),
        // validate with whichever Compose CLI form the host has, then rename
        // atomically. Each stage exits with its own code so the caller maps a
        // typed status without parsing prose.
        var dir = SshCommandExecutor.QuoteForShell(projectPath);
        var file = SshCommandExecutor.QuoteForShell(fileName);
        var next = SshCommandExecutor.QuoteForShell(fileName + ".next");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var script =
            (createDirectory ? $"mkdir -p {dir} 2>/dev/null; " : "") +
            $"cd {dir} 2>/dev/null || exit {ExitDirectoryNotFound}; " +
            $"printf '%s' '{b64}' | base64 -d > {next} || exit {ExitWriteFailed}; " +
            "if docker compose version >/dev/null 2>&1; then DC='docker compose'; " +
            "elif command -v docker-compose >/dev/null 2>&1; then DC='docker-compose'; " +
            $"else rm -f -- {next}; exit {ExitCliNotAvailable}; fi; " +
            $"ERR=$($DC -f {next} config -q 2>&1); ST=$?; " +
            $"if [ $ST -ne 0 ]; then rm -f -- {next}; printf '%s' \"$ERR\" >&2; exit {ExitValidationFailed}; fi; " +
            $"mv -f -- {next} {file} || exit {ExitRenameFailed}; exit 0";

        SshCommandOutcome outcome;
        try
        {
            outcome = await RunSshCommandAsync(ssh, script, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeWriteResult(ComposeWriteStatus.SshFailed,
                $"SSH to {ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        return outcome.ExitStatus switch
        {
            0 => ComposeWriteResult.Ok,
            ExitDirectoryNotFound => new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Compose project directory '{projectPath}' was not found on the host."),
            ExitWriteFailed => new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Writing '{fileName}.next' in '{projectPath}' on the host failed: {Detail(outcome)}"),
            ExitCliNotAvailable => new ComposeWriteResult(ComposeWriteStatus.CliNotAvailable,
                "Cannot validate the edited file: no docker compose CLI was found on the host. The save was refused."),
            ExitValidationFailed => new ComposeWriteResult(ComposeWriteStatus.ValidationFailed,
                string.IsNullOrWhiteSpace(outcome.Stderr)
                    ? "docker compose config rejected the edited file."
                    : outcome.Stderr.Trim()),
            ExitRenameFailed => new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Replacing '{fileName}' in '{projectPath}' on the host failed: {Detail(outcome)}"),
            _ => new ComposeWriteResult(ComposeWriteStatus.SshFailed,
                $"Saving the Compose file over SSH failed: {Detail(outcome)}"),
        };
    }

    public async Task<ComposeWriteResult> ValidateAsync(
        string projectPath, string fileName, string content, CancellationToken cancellationToken = default)
    {
        if (!DirectoryExists(projectPath))
            return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Compose project directory '{projectPath}' was not found inside the container.");

        var nextPath = Path.Combine(projectPath, fileName + ".next");
        try
        {
            await WriteFileAsync(nextPath, content, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Writing '{nextPath}' failed: {ex.Message}");
        }

        var validation = await composeCommandRunner.ValidateConfigAsync(projectPath, nextPath, cancellationToken);
        TryDelete(nextPath);
        return MapValidation(validation);
    }

    public async Task<ComposeWriteResult> ValidateOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, string content,
        CancellationToken cancellationToken = default)
    {
        // Same upload + `config -q` as the write path, but the candidate is always
        // removed and the original is never renamed — a pure dry-run.
        var dir = SshCommandExecutor.QuoteForShell(projectPath);
        var next = SshCommandExecutor.QuoteForShell(fileName + ".next");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var script =
            $"cd {dir} 2>/dev/null || exit {ExitDirectoryNotFound}; " +
            $"printf '%s' '{b64}' | base64 -d > {next} || {{ rm -f -- {next}; exit {ExitWriteFailed}; }}; " +
            "if docker compose version >/dev/null 2>&1; then DC='docker compose'; " +
            "elif command -v docker-compose >/dev/null 2>&1; then DC='docker-compose'; " +
            $"else rm -f -- {next}; exit {ExitCliNotAvailable}; fi; " +
            $"ERR=$($DC -f {next} config -q 2>&1); ST=$?; rm -f -- {next}; " +
            $"if [ $ST -ne 0 ]; then printf '%s' \"$ERR\" >&2; exit {ExitValidationFailed}; fi; exit 0";

        SshCommandOutcome outcome;
        try
        {
            outcome = await RunSshCommandAsync(ssh, script, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeWriteResult(ComposeWriteStatus.SshFailed,
                $"SSH to {ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        return outcome.ExitStatus switch
        {
            0 => ComposeWriteResult.Ok,
            ExitDirectoryNotFound => new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Compose project directory '{projectPath}' was not found on the host."),
            ExitWriteFailed => new ComposeWriteResult(ComposeWriteStatus.WriteFailed,
                $"Writing the candidate file in '{projectPath}' on the host failed: {Detail(outcome)}"),
            ExitCliNotAvailable => new ComposeWriteResult(ComposeWriteStatus.CliNotAvailable,
                "Cannot validate the edited file: no docker compose CLI was found on the host."),
            ExitValidationFailed => new ComposeWriteResult(ComposeWriteStatus.ValidationFailed,
                string.IsNullOrWhiteSpace(outcome.Stderr)
                    ? "docker compose config rejected the edited file."
                    : outcome.Stderr.Trim()),
            _ => new ComposeWriteResult(ComposeWriteStatus.SshFailed,
                $"Validating the Compose file over SSH failed: {Detail(outcome)}"),
        };
    }

    private static ComposeWriteResult MapValidation(ComposeRunResult validation) =>
        validation.IsSuccess
            ? ComposeWriteResult.Ok
            : validation.Status == ComposeRunnerStatus.CliNotAvailable
                ? new ComposeWriteResult(ComposeWriteStatus.CliNotAvailable,
                    "Cannot validate the edited file: the docker compose CLI is not available inside the Stashboard container.")
                : new ComposeWriteResult(ComposeWriteStatus.ValidationFailed,
                    validation.Error ?? "docker compose config rejected the edited file.");

    private void TryDelete(string path)
    {
        try { DeleteFile(path); } catch { /* best effort */ }
    }

    private static string Detail(SshCommandOutcome outcome) =>
        string.IsNullOrWhiteSpace(outcome.Stderr)
            ? $"exit code {outcome.ExitStatus}"
            : outcome.Stderr.Trim();
}
