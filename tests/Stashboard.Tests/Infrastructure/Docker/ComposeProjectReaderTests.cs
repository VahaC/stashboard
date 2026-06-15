using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.0 — unit tests for <see cref="ComposeProjectReader"/>: candidate-name
/// precedence, the directory / file not-found statuses, and that the matched
/// file's content rides back. All branches driven through the filesystem
/// seams — no real I/O.
/// </summary>
public class ComposeProjectReaderTests
{
    private readonly ComposeProjectReader _reader = new();

    [Fact]
    public async Task Read_DirectoryMissing_ReturnsDirectoryNotFound()
    {
        _reader.DirectoryExists = _ => false;

        var result = await _reader.ReadAsync("/compose-projects/home");

        Assert.Equal(ComposeProjectReadStatus.DirectoryNotFound, result.Status);
        Assert.Null(result.FileName);
        Assert.Contains("/compose-projects/home", result.Error);
    }

    [Fact]
    public async Task Read_NoComposeFile_ReturnsFileNotFound()
    {
        _reader.DirectoryExists = _ => true;
        _reader.FileExists = _ => false;

        var result = await _reader.ReadAsync("/compose-projects/home");

        Assert.Equal(ComposeProjectReadStatus.FileNotFound, result.Status);
        Assert.Null(result.FileName);
        Assert.Contains("No Compose file", result.Error);
    }

    [Fact]
    public async Task Read_LegacyFileName_IsMatchedAndRead()
    {
        _reader.DirectoryExists = _ => true;
        _reader.FileExists = path => path.EndsWith("docker-compose.yml");
        _reader.ReadFileAsync = (_, _) => Task.FromResult("services: {}");

        var result = await _reader.ReadAsync("/compose-projects/home");

        Assert.Equal(ComposeProjectReadStatus.Ok, result.Status);
        Assert.Equal("docker-compose.yml", result.FileName);
        Assert.Equal("services: {}", result.Content);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Read_CanonicalNameWins_WhenBothArePresent()
    {
        // The Compose spec's own precedence: compose.yaml before the legacy names.
        var read = new List<string>();
        _reader.DirectoryExists = _ => true;
        _reader.FileExists = _ => true;
        _reader.ReadFileAsync = (path, _) => { read.Add(path); return Task.FromResult("services: {}"); };

        var result = await _reader.ReadAsync("/compose-projects/home");

        Assert.Equal("compose.yaml", result.FileName);
        Assert.Single(read);
    }

    // ── V7.0 — SSH read (remote Docker hosts) ───────────────────────────────

    private static readonly DockerSshCredentials SshCreds = new(
        "vps.example.com", 22, "docker", "PEM", null, "/var/run/docker.sock");

    [Fact]
    public async Task ReadOverSsh_ParsesFileNameAndContent_FromTheSingleRoundTrip()
    {
        string? sentCommand = null;
        _reader.RunSshCommandAsync = (_, command, _) =>
        {
            sentCommand = command;
            return Task.FromResult(new SshCommandOutcome(0, "docker-compose.yml\nservices:\n  web:\n", ""));
        };

        var result = await _reader.ReadOverSshAsync(SshCreds, "/srv/stack");

        Assert.Equal(ComposeProjectReadStatus.Ok, result.Status);
        Assert.Equal("docker-compose.yml", result.FileName);
        Assert.Equal("services:\n  web:\n", result.Content);
        // The probe must target the configured directory (shell-quoted).
        Assert.Contains("cd '/srv/stack'", sentCommand);
        Assert.Contains("compose.yaml", sentCommand);
    }

    [Fact]
    public async Task ReadOverSsh_QuotesAPathContainingSingleQuotes()
    {
        string? sentCommand = null;
        _reader.RunSshCommandAsync = (_, command, _) =>
        {
            sentCommand = command;
            return Task.FromResult(new SshCommandOutcome(0, "compose.yaml\nservices: {}", ""));
        };

        await _reader.ReadOverSshAsync(SshCreds, "/srv/it's here");

        Assert.Contains("cd '/srv/it'\\''s here'", sentCommand);
    }

    [Fact]
    public async Task ReadOverSsh_MapsTheProbeExitCodes_ToTypedStatuses()
    {
        _reader.RunSshCommandAsync = (_, _, _) => Task.FromResult(new SshCommandOutcome(40, "", ""));
        var dirMissing = await _reader.ReadOverSshAsync(SshCreds, "/srv/stack");
        Assert.Equal(ComposeProjectReadStatus.DirectoryNotFound, dirMissing.Status);

        _reader.RunSshCommandAsync = (_, _, _) => Task.FromResult(new SshCommandOutcome(41, "", ""));
        var fileMissing = await _reader.ReadOverSshAsync(SshCreds, "/srv/stack");
        Assert.Equal(ComposeProjectReadStatus.FileNotFound, fileMissing.Status);
    }

    [Fact]
    public async Task ReadOverSsh_UnexpectedExitCode_ReturnsSshFailedWithStderr()
    {
        _reader.RunSshCommandAsync = (_, _, _) =>
            Task.FromResult(new SshCommandOutcome(126, "", "permission denied"));

        var result = await _reader.ReadOverSshAsync(SshCreds, "/srv/stack");

        Assert.Equal(ComposeProjectReadStatus.SshFailed, result.Status);
        Assert.Contains("permission denied", result.Error);
    }

    [Fact]
    public async Task ReadOverSsh_ConnectFailure_ReturnsSshFailed_InsteadOfThrowing()
    {
        _reader.RunSshCommandAsync = (_, _, _) =>
            throw new InvalidOperationException("connection refused");

        var result = await _reader.ReadOverSshAsync(SshCreds, "/srv/stack");

        Assert.Equal(ComposeProjectReadStatus.SshFailed, result.Status);
        Assert.Contains("vps.example.com:22", result.Error);
        Assert.Contains("connection refused", result.Error);
    }
}
