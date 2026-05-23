using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V5.2 — unit tests for <see cref="ComposeCommandRunner"/>. The process
/// launcher and directory probe are swapped for in-memory fakes via the
/// runner's settable seams, so every branch (plugin vs. standalone detection,
/// pull / up success + failure, missing binary, missing project dir) is
/// exercised without spawning a real process or touching the filesystem.
/// </summary>
public class ComposeCommandRunnerTests
{
    private static ComposeCommandRunner NewRunner() => new(NullLogger<ComposeCommandRunner>.Instance);

    private const string ProjectPath = "/compose-projects/home";
    private const string Service = "web";

    // ── detection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task IsAvailable_PluginPresent_ReturnsTrueAndUsesDockerComposeForm()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = NewRunner();
        runner.DirectoryExists = _ => true;
        runner.RunProcessAsync = (spec, _) =>
        {
            calls.Add(spec);
            // `docker compose version` succeeds → plugin form.
            return Task.FromResult(new ProcessRunOutcome(Started: true, ExitCode: 0, "Docker Compose v2", ""));
        };

        Assert.True(await runner.IsAvailableAsync());

        var probe = Assert.Single(calls);
        Assert.Equal("docker", probe.FileName);
        Assert.Equal(new[] { "compose", "version" }, probe.Arguments);
    }

    [Fact]
    public async Task IsAvailable_OnlyStandalonePresent_ReturnsTrueAndUsesDockerComposeBinary()
    {
        var runner = NewRunner();
        runner.DirectoryExists = _ => true;
        runner.RunProcessAsync = (spec, _) =>
        {
            // Plugin probe fails to launch; standalone binary answers.
            if (spec.FileName == "docker")
                return Task.FromException<ProcessRunOutcome>(new System.ComponentModel.Win32Exception("not found"));
            return Task.FromResult(new ProcessRunOutcome(Started: true, ExitCode: 0, "docker-compose 1.29", ""));
        };

        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_NeitherPresent_ReturnsFalse()
    {
        var runner = NewRunner();
        runner.RunProcessAsync = (spec, _) => spec.FileName == "docker"
            ? Task.FromResult(new ProcessRunOutcome(Started: true, ExitCode: 1, "", "unknown command")) // plugin missing
            : Task.FromException<ProcessRunOutcome>(new System.ComponentModel.Win32Exception("not found")); // standalone missing

        Assert.False(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_DetectionIsCachedAcrossCalls()
    {
        var probeCount = 0;
        var runner = NewRunner();
        runner.RunProcessAsync = (_, _) =>
        {
            probeCount++;
            return Task.FromResult(new ProcessRunOutcome(true, 0, "", ""));
        };

        await runner.IsAvailableAsync();
        await runner.IsAvailableAsync();
        await runner.IsAvailableAsync();

        // Only the first call probes; the resolved form is cached.
        Assert.Equal(1, probeCount);
    }

    // ── recreate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Recreate_PluginForm_RunsPullThenUpWithServiceAndProjectDir()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = BuildPluginRunner(calls, pullExit: 0, upExit: 0);

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.Success, result.Status);
        Assert.True(result.IsSuccess);

        // calls[0] = version probe, [1] = pull, [2] = up.
        var pull = calls[1];
        Assert.Equal("docker", pull.FileName);
        Assert.Equal(new[] { "compose", "pull", Service }, pull.Arguments);
        Assert.Equal(ProjectPath, pull.WorkingDirectory);

        var up = calls[2];
        Assert.Equal(new[] { "compose", "up", "-d", Service }, up.Arguments);
        Assert.Equal(ProjectPath, up.WorkingDirectory);
        // No --no-deps: dependencies must come up in depends_on order.
        Assert.DoesNotContain("--no-deps", up.Arguments);
    }

    [Fact]
    public async Task Recreate_StandaloneForm_OmitsComposeSubcommandPrefix()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = NewRunner();
        runner.DirectoryExists = _ => true;
        runner.RunProcessAsync = (spec, _) =>
        {
            calls.Add(spec);
            if (spec.FileName == "docker")
                return Task.FromException<ProcessRunOutcome>(new System.ComponentModel.Win32Exception("not found"));
            return Task.FromResult(new ProcessRunOutcome(true, 0, "", ""));
        };

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.Success, result.Status);
        var pull = calls.Single(c => c.FileName == "docker-compose" && c.Arguments.Contains("pull"));
        Assert.Equal(new[] { "pull", Service }, pull.Arguments);
    }

    [Fact]
    public async Task Recreate_PullFails_ReturnsCommandFailedAndNeverRunsUp()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = BuildPluginRunner(calls, pullExit: 1, upExit: 0, pullStderr: "manifest unknown");

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.CommandFailed, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("manifest unknown", result.Error);
        // up must not run after a failed pull.
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("up"));
    }

    [Fact]
    public async Task Recreate_UpFails_ReturnsCommandFailed()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = BuildPluginRunner(calls, pullExit: 0, upExit: 17, upStderr: "port is already allocated");

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.CommandFailed, result.Status);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("port is already allocated", result.Error);
    }

    [Fact]
    public async Task Recreate_ProjectDirMissing_ReturnsProjectPathNotFoundWithoutShellingOut()
    {
        var calls = new List<ProcessRunSpec>();
        var runner = NewRunner();
        runner.DirectoryExists = _ => false; // bind mount missing
        runner.RunProcessAsync = (spec, _) =>
        {
            calls.Add(spec);
            return Task.FromResult(new ProcessRunOutcome(true, 0, "", ""));
        };

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.ProjectPathNotFound, result.Status);
        Assert.Contains(ProjectPath, result.Error);
        // No pull / up attempted — only the detection probe may have run.
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("pull") || c.Arguments.Contains("up"));
    }

    [Fact]
    public async Task Recreate_CliNotAvailable_ReturnsCliNotAvailable()
    {
        var runner = NewRunner();
        runner.DirectoryExists = _ => true;
        runner.RunProcessAsync = (_, _) =>
            Task.FromException<ProcessRunOutcome>(new System.ComponentModel.Win32Exception("not found"));

        var result = await runner.RecreateServiceAsync(new ComposeRecreateRequest(ProjectPath, Service));

        Assert.Equal(ComposeRunnerStatus.CliNotAvailable, result.Status);
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    private static ComposeCommandRunner BuildPluginRunner(
        List<ProcessRunSpec> calls, int pullExit, int upExit,
        string? pullStderr = null, string? upStderr = null)
    {
        var runner = NewRunner();
        runner.DirectoryExists = _ => true;
        runner.RunProcessAsync = (spec, _) =>
        {
            calls.Add(spec);
            if (spec.Arguments.Contains("version"))
                return Task.FromResult(new ProcessRunOutcome(true, 0, "Docker Compose v2", ""));
            if (spec.Arguments.Contains("pull"))
                return Task.FromResult(new ProcessRunOutcome(true, pullExit, "", pullStderr ?? ""));
            return Task.FromResult(new ProcessRunOutcome(true, upExit, "", upStderr ?? ""));
        };
        return runner;
    }
}
