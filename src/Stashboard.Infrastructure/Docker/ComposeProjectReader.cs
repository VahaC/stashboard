using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.0 — production <see cref="IComposeProjectReader"/>. Probes a project
/// directory for the standard Compose file names (in the Compose spec's own
/// precedence order) and returns the raw YAML text. Two transports:
/// the local filesystem (the V5.2 bind mount, LocalSocket connections) and a
/// remote read over the connection's SSH credentials (Ssh connections).
/// Read-only: never writes and never invokes the Compose CLI.
/// </summary>
/// <remarks>The directory / file probes, the file read, and the SSH command
/// execution are exposed as settable seams (same pattern as
/// <see cref="ComposeCommandRunner"/>) so unit tests can drive every branch
/// without touching the real filesystem or opening a real SSH session.</remarks>
public sealed class ComposeProjectReader : IComposeProjectReader
{
    /// <summary>Hard cap on the remote file size (1 MiB) — a Compose file is a
    /// few KB; anything bigger is a misconfigured path, not a project.</summary>
    private const int MaxRemoteFileBytes = 1024 * 1024;

    /// <summary>Exit codes the remote probe script uses to signal the typed
    /// not-found states (picked outside the 0–2 shell-builtin range).</summary>
    private const int ExitDirectoryNotFound = 40;
    private const int ExitFileNotFound = 41;

    /// <summary>Compose spec precedence: <c>compose.yaml</c> is canonical, the
    /// <c>docker-compose.*</c> names are the legacy fallbacks.</summary>
    private static readonly string[] CandidateFileNames =
        { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" };

    /// <summary>Test seam — checks whether the Compose project directory exists.</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    /// <summary>Test seam — checks whether a candidate Compose file exists.</summary>
    public Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>Test seam — reads the matched Compose file.</summary>
    public Func<string, CancellationToken, Task<string>> ReadFileAsync { get; set; }
        = (path, ct) => File.ReadAllTextAsync(path, ct);

    public async Task<ComposeProjectReadResult> ReadAsync(
        string projectPath, CancellationToken cancellationToken = default)
    {
        if (!DirectoryExists(projectPath))
            return new ComposeProjectReadResult(ComposeProjectReadStatus.DirectoryNotFound, null, null,
                $"Compose project directory '{projectPath}' was not found inside the container.");

        foreach (var fileName in CandidateFileNames)
        {
            var fullPath = Path.Combine(projectPath, fileName);
            if (!FileExists(fullPath)) continue;
            var content = await ReadFileAsync(fullPath, cancellationToken);
            return new ComposeProjectReadResult(ComposeProjectReadStatus.Ok, fileName, content, null);
        }

        return new ComposeProjectReadResult(ComposeProjectReadStatus.FileNotFound, null, null,
            $"No Compose file (compose.yaml / docker-compose.yml) found in '{projectPath}'.");
    }

    // ── V7.0 — SSH read (remote Docker hosts) ───────────────────────────────

    /// <summary>Test seam — runs one command over SSH and returns exit status +
    /// captured stdout/stderr. Production delegates to the shared
    /// <see cref="SshCommandExecutor"/> (short-lived client per call).</summary>
    public Func<DockerSshCredentials, string, CancellationToken, Task<SshCommandOutcome>> RunSshCommandAsync { get; set; }
        = SshCommandExecutor.RunAsync;

    public async Task<ComposeProjectReadResult> ReadOverSshAsync(
        DockerSshCredentials ssh, string projectPath, CancellationToken cancellationToken = default)
    {
        // One round trip: cd into the project dir (distinct exit code when it
        // doesn't exist), find the first candidate file, print its name on the
        // first line and its content after — so the caller learns the matched
        // file name without a second command.
        var script =
            $"cd {QuoteForShell(projectPath)} 2>/dev/null || exit {ExitDirectoryNotFound}; " +
            $"for f in {string.Join(' ', CandidateFileNames)}; do " +
            "if [ -f \"$f\" ]; then printf '%s\\n' \"$f\"; cat -- \"$f\"; exit 0; fi; done; " +
            $"exit {ExitFileNotFound}";

        SshCommandOutcome outcome;
        try
        {
            outcome = await RunSshCommandAsync(ssh, script, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeProjectReadResult(ComposeProjectReadStatus.SshFailed, null, null,
                $"SSH to {ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        switch (outcome.ExitStatus)
        {
            case 0:
                break;
            case ExitDirectoryNotFound:
                return new ComposeProjectReadResult(ComposeProjectReadStatus.DirectoryNotFound, null, null,
                    $"Compose project directory '{projectPath}' was not found on the host.");
            case ExitFileNotFound:
                return new ComposeProjectReadResult(ComposeProjectReadStatus.FileNotFound, null, null,
                    $"No Compose file (compose.yaml / docker-compose.yml) found in '{projectPath}' on the host.");
            default:
                var detail = string.IsNullOrWhiteSpace(outcome.Stderr)
                    ? $"exit code {outcome.ExitStatus}"
                    : outcome.Stderr.Trim();
                return new ComposeProjectReadResult(ComposeProjectReadStatus.SshFailed, null, null,
                    $"Reading the Compose file over SSH failed: {detail}");
        }

        // First line = matched file name, the rest = file content.
        var stdout = outcome.Stdout;
        var newline = stdout.IndexOf('\n');
        if (newline < 0)
            return new ComposeProjectReadResult(ComposeProjectReadStatus.SshFailed, null, null,
                "Reading the Compose file over SSH returned no content.");
        var fileName = stdout[..newline].TrimEnd('\r').Trim();
        var content = stdout[(newline + 1)..];

        if (content.Length > MaxRemoteFileBytes)
            return new ComposeProjectReadResult(ComposeProjectReadStatus.SshFailed, null, null,
                $"'{fileName}' is larger than {MaxRemoteFileBytes / 1024} KiB — not a Compose file?");

        return new ComposeProjectReadResult(ComposeProjectReadStatus.Ok, fileName, content, null);
    }

    /// <summary>POSIX single-quote escaping so a path with spaces (or quotes)
    /// survives the remote shell.</summary>
    private static string QuoteForShell(string value) => SshCommandExecutor.QuoteForShell(value);
}

/// <summary>V7.0 — outcome of one SSH command for the reader's SSH seam.</summary>
public sealed record SshCommandOutcome(int ExitStatus, string Stdout, string Stderr);
