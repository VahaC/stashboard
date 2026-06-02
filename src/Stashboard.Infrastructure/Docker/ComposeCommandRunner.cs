using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V5.2 — production <see cref="IComposeCommandRunner"/>. Detects which Compose
/// CLI form is installed (the modern <c>docker compose</c> plugin or the legacy
/// standalone <c>docker-compose</c> binary), caches the answer, and shells out
/// <c>pull</c> + <c>up -d</c> for a single service.
/// </summary>
/// <remarks>
/// <para>The compose CLI inside the Stashboard container talks to the local
/// Docker socket; this path is therefore only used for
/// <c>LocalSocket</c> connections (the caller enforces that).</para>
/// <para>Both the process launcher and the directory probe are exposed as
/// settable seams so unit tests can drive every branch without spawning a real
/// process or touching the filesystem — production wires the real
/// implementations in the constructor.</para>
/// </remarks>
public sealed class ComposeCommandRunner(ILogger<ComposeCommandRunner> logger) : IComposeCommandRunner
{
    /// <summary>Hard cap on a single compose invocation. A pull of a large image
    /// over a slow link can legitimately take a while, so this is generous.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _detectLock = new(1, 1);
    private ComposeInvocation? _resolved;
    private bool _detectionComplete;

    /// <summary>Test seam — runs a process and returns its outcome. Production
    /// uses <see cref="RunProcessRealAsync"/>.</summary>
    public Func<ProcessRunSpec, CancellationToken, Task<ProcessRunOutcome>> RunProcessAsync { get; set; }
        = RunProcessRealAsync;

    /// <summary>Test seam — checks whether the Compose project directory exists.</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await ResolveInvocationAsync(cancellationToken) is not null;

    public async Task<ComposeRunResult> RecreateServiceAsync(
        ComposeRecreateRequest request, CancellationToken cancellationToken = default)
    {
        var invocation = await ResolveInvocationAsync(cancellationToken);
        if (invocation is null)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, null,
                "The docker compose CLI is not available inside the Stashboard container.");

        if (!DirectoryExists(request.ProjectPath))
            return new ComposeRunResult(ComposeRunnerStatus.ProjectPathNotFound, null, null,
                $"Compose project directory '{request.ProjectPath}' was not found inside the container.");

        // 1) Pull the service's image first so `up -d` actually swaps to the new
        //    image (a fixed tag whose digest moved would otherwise be a no-op).
        var pull = await RunComposeAsync(invocation, request.ProjectPath,
            new[] { "pull", request.ServiceName }, cancellationToken);
        if (!pull.Started)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, pull.Stdout, pull.Stderr);
        if (pull.ExitCode != 0)
            return new ComposeRunResult(ComposeRunnerStatus.CommandFailed, pull.ExitCode, pull.Stdout,
                $"docker compose pull failed: {Describe(pull)}");

        // 2) Recreate. No --no-deps: Compose brings dependencies up in
        //    depends_on order, which is the whole point of this path.
        var up = await RunComposeAsync(invocation, request.ProjectPath,
            new[] { "up", "-d", request.ServiceName }, cancellationToken);
        if (!up.Started)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, up.Stdout, up.Stderr);
        if (up.ExitCode != 0)
            return new ComposeRunResult(ComposeRunnerStatus.CommandFailed, up.ExitCode, up.Stdout,
                $"docker compose up -d failed: {Describe(up)}");

        return new ComposeRunResult(ComposeRunnerStatus.Success, 0, up.Stdout, null);
    }

    public async Task<ComposeRunResult> RecreateProjectAsync(
        ComposeProjectRecreateRequest request, CancellationToken cancellationToken = default)
    {
        var invocation = await ResolveInvocationAsync(cancellationToken);
        if (invocation is null)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, null,
                "The docker compose CLI is not available inside the Stashboard container.");

        if (!DirectoryExists(request.ProjectPath))
            return new ComposeRunResult(ComposeRunnerStatus.ProjectPathNotFound, null, null,
                $"Compose project directory '{request.ProjectPath}' was not found inside the container.");

        // V5.4 — bulk update: pull every image referenced by the file, then
        // bring the whole project back up in one shot. No service argument is
        // passed so Compose handles depends_on ordering itself.
        var pull = await RunComposeAsync(invocation, request.ProjectPath,
            new[] { "pull" }, cancellationToken);
        if (!pull.Started)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, pull.Stdout, pull.Stderr);
        if (pull.ExitCode != 0)
            return new ComposeRunResult(ComposeRunnerStatus.CommandFailed, pull.ExitCode, pull.Stdout,
                $"docker compose pull failed: {Describe(pull)}");

        var up = await RunComposeAsync(invocation, request.ProjectPath,
            new[] { "up", "-d" }, cancellationToken);
        if (!up.Started)
            return new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, up.Stdout, up.Stderr);
        if (up.ExitCode != 0)
            return new ComposeRunResult(ComposeRunnerStatus.CommandFailed, up.ExitCode, up.Stdout,
                $"docker compose up -d failed: {Describe(up)}");

        return new ComposeRunResult(ComposeRunnerStatus.Success, 0, up.Stdout, null);
    }

    // ── detection ──────────────────────────────────────────────────────────────

    private async Task<ComposeInvocation?> ResolveInvocationAsync(CancellationToken cancellationToken)
    {
        if (_detectionComplete) return _resolved;

        await _detectLock.WaitAsync(cancellationToken);
        try
        {
            if (_detectionComplete) return _resolved;

            // Prefer the modern plugin (`docker compose`); fall back to the
            // legacy standalone binary (`docker-compose`).
            foreach (var candidate in new[]
                     {
                         new ComposeInvocation("docker", new[] { "compose" }),
                         new ComposeInvocation("docker-compose", Array.Empty<string>()),
                     })
            {
                ProcessRunOutcome probe;
                try
                {
                    probe = await RunProcessAsync(
                        new ProcessRunSpec(
                            candidate.FileName,
                            candidate.Prefix.Concat(new[] { "version" }).ToList(),
                            WorkingDirectory: null),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    // A missing binary surfaces as Win32Exception / file-not-found;
                    // treat any launch failure as "this form isn't installed".
                    logger.LogDebug(ex, "Compose CLI probe '{File}' failed to launch", candidate.FileName);
                    continue;
                }

                if (probe.Started && probe.ExitCode == 0)
                {
                    _resolved = candidate;
                    break;
                }
            }

            _detectionComplete = true;
            return _resolved;
        }
        finally
        {
            _detectLock.Release();
        }
    }

    private Task<ProcessRunOutcome> RunComposeAsync(
        ComposeInvocation invocation, string projectPath, string[] composeArgs, CancellationToken cancellationToken)
    {
        var args = invocation.Prefix.Concat(composeArgs).ToList();
        return RunProcessAsync(new ProcessRunSpec(invocation.FileName, args, projectPath), cancellationToken);
    }

    private static string Describe(ProcessRunOutcome outcome)
    {
        var stderr = outcome.Stderr?.Trim();
        if (!string.IsNullOrEmpty(stderr)) return stderr;
        var stdout = outcome.Stdout?.Trim();
        return string.IsNullOrEmpty(stdout) ? $"exit code {outcome.ExitCode}" : stdout;
    }

    // ── real process launcher ────────────────────────────────────────────────

    private static async Task<ProcessRunOutcome> RunProcessRealAsync(
        ProcessRunSpec spec, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(spec.WorkingDirectory)) psi.WorkingDirectory = spec.WorkingDirectory;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            throw;
        }

        return new ProcessRunOutcome(Started: true, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record ComposeInvocation(string FileName, IReadOnlyList<string> Prefix);
}

/// <summary>V5.2 — describes one external process invocation for the runner's
/// process seam.</summary>
public sealed record ProcessRunSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

/// <summary>V5.2 — outcome of an external process invocation. <see cref="Started"/>
/// is false when the binary could not be launched at all (not installed).</summary>
public sealed record ProcessRunOutcome(bool Started, int ExitCode, string Stdout, string Stderr);
