using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V7.6 — unit tests for <see cref="ComposeHistoryStore"/>: the local
/// snapshot/list/read against an in-memory filesystem (dedupe + prune included)
/// and the SSH script shape + list parsing. No real filesystem or SSH session.
/// </summary>
public class ComposeHistoryStoreTests
{
    private static readonly DockerSshCredentials Ssh =
        new("vps.example.com", 22, "docker", "PEM", null, "/var/run/docker.sock");

    /// <summary>In-memory file store keyed by the paths the store builds via
    /// Path.Combine; the seams read/write/list/delete against it.</summary>
    private sealed class FakeFs
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);

        public ComposeHistoryStore Build() => new()
        {
            FileExists = p => Files.ContainsKey(p),
            DirectoryExists = _ => true,
            CreateDirectory = _ => { },
            ReadFileAsync = (p, _) => Task.FromResult(Files[p]),
            WriteFileAsync = (p, c, _) => { Files[p] = c; return Task.CompletedTask; },
            DeleteFile = p => Files.Remove(p),
            ListDirectory = dir => Files.Keys
                .Where(k => k.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Select(k => k[(dir.Length + 1)..])
                .ToList(),
            FileSize = p => Files[p].Length,
        };

        public string HistoryDir(string project) => Path.Combine(project, ".stashboard", "history");
    }

    [Fact]
    public async Task SnapshotAsync_FirstSave_WritesOneRevision()
    {
        var fs = new FakeFs();
        fs.Files[Path.Combine("/proj", "docker-compose.yml")] = "services: {}\n";
        var store = fs.Build();

        await store.SnapshotAsync("/proj", "docker-compose.yml");

        var historyFiles = fs.Files.Keys.Where(k => k.Contains(".stashboard")).ToList();
        var entry = Assert.Single(historyFiles);
        Assert.EndsWith("__docker-compose.yml", entry);
        Assert.Equal("services: {}\n", fs.Files[entry]);
    }

    [Fact]
    public async Task SnapshotAsync_NoSourceFile_IsNoOp()
    {
        var fs = new FakeFs();
        var store = fs.Build();

        await store.SnapshotAsync("/proj", "docker-compose.yml");

        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task SnapshotAsync_NewestIdentical_SkipsDuplicate()
    {
        var fs = new FakeFs();
        var content = "services: {}\n";
        fs.Files[Path.Combine("/proj", "docker-compose.yml")] = content;
        // A prior revision with the same content.
        fs.Files[Path.Combine(fs.HistoryDir("/proj"), "20200101T120000000__docker-compose.yml")] = content;
        var store = fs.Build();

        await store.SnapshotAsync("/proj", "docker-compose.yml");

        Assert.Single(fs.Files.Keys, k => k.Contains(".stashboard"));
    }

    [Fact]
    public async Task SnapshotAsync_PrunesBeyondKeepLast()
    {
        var fs = new FakeFs();
        fs.Files[Path.Combine("/proj", "docker-compose.yml")] = "current\n";
        // 20 older revisions, oldest first.
        for (var i = 0; i < 20; i++)
            fs.Files[Path.Combine(fs.HistoryDir("/proj"), $"20200101T1200000{i:D2}__docker-compose.yml")] = $"rev{i}\n";
        var store = fs.Build();

        await store.SnapshotAsync("/proj", "docker-compose.yml");

        var historyCount = fs.Files.Keys.Count(k => k.Contains(".stashboard"));
        Assert.Equal(20, historyCount); // 20 + 1 new − 1 pruned
        // The oldest (…00) was pruned; the new "current" revision is kept.
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith("20200101T120000000__docker-compose.yml"));
        Assert.Contains(fs.Files.Values, v => v == "current\n");
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirstWithSizes()
    {
        var fs = new FakeFs();
        fs.Files[Path.Combine(fs.HistoryDir("/proj"), "20260101T120000000__docker-compose.yml")] = "aaa";
        fs.Files[Path.Combine(fs.HistoryDir("/proj"), "20260102T120000000__docker-compose.yml")] = "bbbbb";
        var store = fs.Build();

        var result = await store.ListAsync("/proj", "docker-compose.yml");

        Assert.Equal(ComposeHistoryStatus.Ok, result.Status);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("20260102T120000000__docker-compose.yml", result.Entries[0].Id); // newest first
        Assert.Equal(5, result.Entries[0].SizeBytes);
        Assert.Equal(3, result.Entries[1].SizeBytes);
    }

    [Fact]
    public async Task ReadAsync_RejectsPathTraversal()
    {
        var store = new FakeFs().Build();

        var result = await store.ReadAsync("/proj", "../../etc/passwd");

        Assert.Equal(ComposeHistoryStatus.NotFound, result.Status);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ReadAsync_ReturnsRevisionContent()
    {
        var fs = new FakeFs();
        fs.Files[Path.Combine(fs.HistoryDir("/proj"), "20260101T120000000__docker-compose.yml")] = "old content\n";
        var store = fs.Build();

        var result = await store.ReadAsync("/proj", "20260101T120000000__docker-compose.yml");

        Assert.Equal(ComposeHistoryStatus.Ok, result.Status);
        Assert.Equal("old content\n", result.Content);
    }

    [Fact]
    public async Task SnapshotOverSshAsync_ScriptSnapshotsDedupesAndPrunes()
    {
        string? script = null;
        var store = new ComposeHistoryStore
        {
            RunSshCommandAsync = (_, s, _) => { script = s; return Task.FromResult(new SshCommandOutcome(0, "", "")); },
        };

        await store.SnapshotOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml");

        Assert.NotNull(script);
        Assert.Contains("mkdir -p", script);
        Assert.Contains("cmp -s", script);              // dedupe against newest
        Assert.Contains(".stashboard/history", script);
        Assert.Contains("tail -n +21", script);          // prune beyond KeepLast (20)
    }

    [Fact]
    public async Task ListOverSshAsync_ParsesTabSeparatedOutput()
    {
        var store = new ComposeHistoryStore
        {
            RunSshCommandAsync = (_, _, _) => Task.FromResult(new SshCommandOutcome(0,
                "20260101T120000000__docker-compose.yml\t128\n20260102T120000000__docker-compose.yml\t256\n", "")),
        };

        var result = await store.ListOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml");

        Assert.Equal(ComposeHistoryStatus.Ok, result.Status);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("20260102T120000000__docker-compose.yml", result.Entries[0].Id); // newest first
        Assert.Equal(256, result.Entries[0].SizeBytes);
    }

    [Fact]
    public async Task SnapshotOverSshAsync_TransportThrow_DoesNotPropagate()
    {
        var store = new ComposeHistoryStore
        {
            RunSshCommandAsync = (_, _, _) => throw new InvalidOperationException("connection refused"),
        };

        // Best-effort: a snapshot failure must never bubble up and block the save.
        await store.SnapshotOverSshAsync(Ssh, "/srv/stack", "docker-compose.yml");
    }
}
