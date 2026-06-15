using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.1 — unit tests for <see cref="ComposeProjectWriter"/>: the atomic
/// write-validate-rename sequence on both transports, the blocking-validation
/// decision (no CLI → no save), the rollback (temp file deleted, original
/// untouched) and the typed SSH exit-code mapping.
/// </summary>
public class ComposeProjectWriterTests
{
    private static readonly DockerSshCredentials Ssh =
        new("vps.example.com", 22, "docker", "PEM", null, "/var/run/docker.sock");

    private readonly Mock<IComposeCommandRunner> _runner = new();

    private ComposeProjectWriter BuildWriter(
        List<(string Path, string Content)> writes,
        List<(string From, string To)> moves,
        List<string> deletes,
        bool directoryExists = true)
    {
        return new ComposeProjectWriter(_runner.Object)
        {
            DirectoryExists = _ => directoryExists,
            WriteFileAsync = (path, content, _) => { writes.Add((path, content)); return Task.CompletedTask; },
            MoveFile = (from, to) => moves.Add((from, to)),
            DeleteFile = deletes.Add,
        };
    }

    // ── local transport ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_HappyPath_WritesNextValidatesThenRenames()
    {
        var writes = new List<(string, string)>();
        var moves = new List<(string, string)>();
        var deletes = new List<string>();
        _runner
            .Setup(r => r.ValidateConfigAsync("/compose/media", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "", null));
        var writer = BuildWriter(writes, moves, deletes);

        var result = await writer.WriteAsync("/compose/media", "docker-compose.yml", "services: {}\n");

        Assert.True(result.IsSuccess);
        var written = Assert.Single(writes);
        Assert.EndsWith("docker-compose.yml.next", written.Item1);
        Assert.Equal("services: {}\n", written.Item2);
        var move = Assert.Single(moves);
        Assert.EndsWith("docker-compose.yml.next", move.Item1);
        Assert.EndsWith("docker-compose.yml", move.Item2);
        Assert.Empty(deletes);

        // Validation ran against the candidate, not the original.
        _runner.Verify(r => r.ValidateConfigAsync(
            "/compose/media", It.Is<string>(p => p.EndsWith("docker-compose.yml.next")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_ValidationFails_DeletesCandidateAndNeverRenames()
    {
        var writes = new List<(string, string)>();
        var moves = new List<(string, string)>();
        var deletes = new List<string>();
        _runner
            .Setup(r => r.ValidateConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.CommandFailed, 1, null,
                "services.web.ports contains an invalid type"));
        var writer = BuildWriter(writes, moves, deletes);

        var result = await writer.WriteAsync("/compose/media", "docker-compose.yml", "bad");

        Assert.Equal(ComposeWriteStatus.ValidationFailed, result.Status);
        Assert.Contains("invalid type", result.Error);
        Assert.Empty(moves);
        var deleted = Assert.Single(deletes);
        Assert.EndsWith("docker-compose.yml.next", deleted);
    }

    [Fact]
    public async Task WriteAsync_CliNotAvailable_RefusesTheSave()
    {
        // V7.1 decision: validation is blocking — no CLI means no write.
        var writes = new List<(string, string)>();
        var moves = new List<(string, string)>();
        var deletes = new List<string>();
        _runner
            .Setup(r => r.ValidateConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.CliNotAvailable, null, null, "no CLI"));
        var writer = BuildWriter(writes, moves, deletes);

        var result = await writer.WriteAsync("/compose/media", "docker-compose.yml", "services: {}\n");

        Assert.Equal(ComposeWriteStatus.CliNotAvailable, result.Status);
        Assert.Contains("refused", result.Error);
        Assert.Empty(moves);
        Assert.Single(deletes);
    }

    [Fact]
    public async Task WriteAsync_MissingDirectory_FailsWithoutWriting()
    {
        var writes = new List<(string, string)>();
        var writer = BuildWriter(writes, new List<(string, string)>(), new List<string>(), directoryExists: false);

        var result = await writer.WriteAsync("/missing", "docker-compose.yml", "x");

        Assert.Equal(ComposeWriteStatus.WriteFailed, result.Status);
        Assert.Empty(writes);
        _runner.VerifyNoOtherCalls();
    }

    // ── SSH transport ───────────────────────────────────────────────────────

    private static ComposeProjectWriter BuildSshWriter(
        Func<string, SshCommandOutcome> respond, List<string>? scripts = null) =>
        new(Mock.Of<IComposeCommandRunner>())
        {
            RunSshCommandAsync = (_, script, _) =>
            {
                scripts?.Add(script);
                return Task.FromResult(respond(script));
            },
        };

    [Fact]
    public async Task WriteOverSshAsync_HappyPath_UploadsValidatesAndRenamesInOneScript()
    {
        var scripts = new List<string>();
        var writer = BuildSshWriter(_ => new SshCommandOutcome(0, "", ""), scripts);

        var result = await writer.WriteOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml", "services: {}\n");

        Assert.True(result.IsSuccess);
        var script = Assert.Single(scripts);
        // Upload via base64 (shell-safe), validate the .next candidate with
        // either CLI form, then atomic rename.
        Assert.Contains("base64 -d > 'docker-compose.yml.next'", script);
        Assert.Contains("docker compose version", script);
        Assert.Contains("command -v docker-compose", script);
        Assert.Contains("config -q", script);
        Assert.Contains("mv -f -- 'docker-compose.yml.next' 'docker-compose.yml'", script);
    }

    [Fact]
    public async Task WriteOverSshAsync_ValidationFailure_SurfacesRemoteStderr()
    {
        var writer = BuildSshWriter(_ => new SshCommandOutcome(44, "",
            "service \"web\" has neither an image nor a build context"));

        var result = await writer.WriteOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml", "bad");

        Assert.Equal(ComposeWriteStatus.ValidationFailed, result.Status);
        Assert.Contains("neither an image nor a build context", result.Error);
    }

    [Theory]
    [InlineData(40, ComposeWriteStatus.WriteFailed)]   // project dir missing
    [InlineData(42, ComposeWriteStatus.CliNotAvailable)]
    [InlineData(43, ComposeWriteStatus.WriteFailed)]   // upload failed
    [InlineData(45, ComposeWriteStatus.WriteFailed)]   // rename failed
    [InlineData(127, ComposeWriteStatus.SshFailed)]    // anything else
    public async Task WriteOverSshAsync_MapsExitCodesToTypedStatuses(int exitCode, ComposeWriteStatus expected)
    {
        var writer = BuildSshWriter(_ => new SshCommandOutcome(exitCode, "", "boom"));

        var result = await writer.WriteOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml", "x");

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task WriteOverSshAsync_TransportException_ReturnsSshFailedWithoutThrowing()
    {
        var writer = new ComposeProjectWriter(Mock.Of<IComposeCommandRunner>())
        {
            RunSshCommandAsync = (_, _, _) => throw new InvalidOperationException("connection refused"),
        };

        var result = await writer.WriteOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml", "x");

        Assert.Equal(ComposeWriteStatus.SshFailed, result.Status);
        Assert.Contains("connection refused", result.Error);
    }

    // ── V7.4.1 — create directory (new project) ────────────────────────────────

    [Fact]
    public async Task WriteAsync_CreateDirectory_CreatesDirThenWrites()
    {
        var created = new List<string>();
        var dirExists = false;
        _runner
            .Setup(r => r.ValidateConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "", null));
        var writer = new ComposeProjectWriter(_runner.Object)
        {
            DirectoryExists = _ => dirExists,
            CreateDirectory = p => { created.Add(p); dirExists = true; },
            WriteFileAsync = (_, _, _) => Task.CompletedTask,
            MoveFile = (_, _) => { },
            DeleteFile = _ => { },
        };

        var result = await writer.WriteAsync("/opt/stacks/new", "docker-compose.yml", "name: x\n", createDirectory: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("/opt/stacks/new", Assert.Single(created));
    }

    [Fact]
    public async Task WriteAsync_CreateDirectoryFails_ReturnsWriteFailed()
    {
        var writer = new ComposeProjectWriter(_runner.Object)
        {
            DirectoryExists = _ => false,
            CreateDirectory = _ => throw new UnauthorizedAccessException("permission denied"),
        };

        var result = await writer.WriteAsync("/root/locked", "docker-compose.yml", "name: x\n", createDirectory: true);

        Assert.Equal(ComposeWriteStatus.WriteFailed, result.Status);
        Assert.Contains("permission denied", result.Error);
    }

    [Fact]
    public async Task WriteOverSshAsync_CreateDirectory_PrependsMkdir()
    {
        var scripts = new List<string>();
        var writer = BuildSshWriter(_ => new SshCommandOutcome(0, "", ""), scripts);

        var result = await writer.WriteOverSshAsync(Ssh, "/srv/new", "docker-compose.yml", "name: x\n", createDirectory: true);

        Assert.True(result.IsSuccess);
        var script = Assert.Single(scripts);
        Assert.Contains("mkdir -p '/srv/new'", script);
    }

    [Fact]
    public async Task WriteOverSshAsync_WithoutCreateDirectory_HasNoMkdir()
    {
        var scripts = new List<string>();
        var writer = BuildSshWriter(_ => new SshCommandOutcome(0, "", ""), scripts);

        await writer.WriteOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml", "name: x\n");

        Assert.DoesNotContain("mkdir -p", Assert.Single(scripts));
    }
}
