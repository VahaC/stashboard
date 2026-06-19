using System.Globalization;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.6 — production <see cref="IComposeHistoryStore"/>. Keeps the last
/// <see cref="KeepLast"/> revisions of a project's Compose file under
/// <c>&lt;project&gt;/.stashboard/history/&lt;stamp&gt;__&lt;file&gt;</c> on both
/// transports (local bind mount + SSH). The stamp is a UTC
/// <c>yyyyMMddTHHmmssfff</c> so the names sort chronologically — listing and
/// pruning both rely on lexicographic order, no file mtimes needed (which keeps
/// the two transports behaving identically).
/// </summary>
/// <remarks>The filesystem and SSH operations are exposed as settable seams (the
/// same pattern as <see cref="ComposeProjectReader"/> / <see cref="ComposeProjectWriter"/>)
/// so unit tests drive every branch without a real filesystem or SSH session.</remarks>
public sealed class ComposeHistoryStore : IComposeHistoryStore
{
    public int KeepLast => 20;

    /// <summary>The <c>.stashboard/history</c> sub-path, relative to the project
    /// directory; shared by both transports for one canonical layout.</summary>
    public const string HistoryRelativeDir = ".stashboard/history";

    private const string StampFormat = "yyyyMMdd'T'HHmmssfff";
    private const string Separator = "__";

    // ── local seams ─────────────────────────────────────────────────────────
    public Func<string, bool> FileExists { get; set; } = File.Exists;
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;
    public Action<string> CreateDirectory { get; set; } = p => Directory.CreateDirectory(p);
    public Func<string, CancellationToken, Task<string>> ReadFileAsync { get; set; }
        = (p, ct) => File.ReadAllTextAsync(p, ct);
    public Func<string, string, CancellationToken, Task> WriteFileAsync { get; set; }
        = (p, c, ct) => File.WriteAllTextAsync(p, c, ct);
    public Action<string> DeleteFile { get; set; } = File.Delete;
    /// <summary>Lists the bare file names in the given directory (empty when it
    /// does not exist).</summary>
    public Func<string, IReadOnlyList<string>> ListDirectory { get; set; }
        = dir => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToList()! : [];
    public Func<string, long> FileSize { get; set; } = p => new FileInfo(p).Length;

    /// <summary>SSH seam — runs one command over SSH. Production delegates to the
    /// shared <see cref="SshCommandExecutor"/>.</summary>
    public Func<DockerSshCredentials, string, CancellationToken, Task<SshCommandOutcome>> RunSshCommandAsync { get; set; }
        = SshCommandExecutor.RunAsync;

    // ── local ────────────────────────────────────────────────────────────────

    public async Task SnapshotAsync(string projectPath, string fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var sourcePath = Path.Combine(projectPath, fileName);
            if (!FileExists(sourcePath)) return;

            var current = await ReadFileAsync(sourcePath, cancellationToken);

            var historyDir = Path.Combine(projectPath, ".stashboard", "history");
            var existing = OrderedEntries(ListDirectory(historyDir), fileName); // oldest → newest

            // Dedupe: skip when the current file already matches the newest kept
            // revision, so repeated saves of the same text don't pile up.
            if (existing.Count > 0)
            {
                var newestPath = Path.Combine(historyDir, existing[^1].Id);
                if (FileExists(newestPath))
                {
                    var newest = await ReadFileAsync(newestPath, cancellationToken);
                    if (string.Equals(newest, current, StringComparison.Ordinal)) return;
                }
            }

            CreateDirectory(historyDir);
            var entryId = $"{DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture)}{Separator}{fileName}";
            await WriteFileAsync(Path.Combine(historyDir, entryId), current, cancellationToken);

            // Prune oldest-first beyond KeepLast (the just-written entry included).
            var afterWrite = OrderedEntries(ListDirectory(historyDir), fileName);
            var excess = afterWrite.Count - KeepLast;
            for (var i = 0; i < excess; i++)
            {
                try { DeleteFile(Path.Combine(historyDir, afterWrite[i].Id)); }
                catch { /* best effort */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* history is best-effort — never block the save */ }
    }

    public Task<ComposeHistoryListResult> ListAsync(
        string projectPath, string fileName, CancellationToken cancellationToken = default)
    {
        var historyDir = Path.Combine(projectPath, ".stashboard", "history");
        var entries = OrderedEntries(ListDirectory(historyDir), fileName)
            .Select(e => e with { SizeBytes = SafeSize(Path.Combine(historyDir, e.Id)) })
            .Reverse() // newest first for the UI
            .ToList();
        return Task.FromResult(ComposeHistoryListResult.Ok(entries));
    }

    public async Task<ComposeHistoryReadResult> ReadAsync(
        string projectPath, string entryId, CancellationToken cancellationToken = default)
    {
        if (!IsBareName(entryId))
            return new ComposeHistoryReadResult(ComposeHistoryStatus.NotFound, null, "Invalid history entry id.");

        var path = Path.Combine(projectPath, ".stashboard", "history", entryId);
        if (!FileExists(path))
            return new ComposeHistoryReadResult(ComposeHistoryStatus.NotFound, null,
                $"History revision '{entryId}' was not found.");

        var content = await ReadFileAsync(path, cancellationToken);
        return new ComposeHistoryReadResult(ComposeHistoryStatus.Ok, content, null);
    }

    private long SafeSize(string path)
    {
        try { return FileSize(path); } catch { return 0; }
    }

    // ── SSH ────────────────────────────────────────────────────────────────────

    public async Task SnapshotOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, CancellationToken cancellationToken = default)
    {
        var dir = SshCommandExecutor.QuoteForShell(projectPath);
        var file = SshCommandExecutor.QuoteForShell(fileName);
        var glob = "*" + Separator + SshCommandExecutor.QuoteForShell(fileName); // '*' globs, suffix literal
        var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        var keepPlusOne = KeepLast + 1;

        // Best-effort throughout: any failure just leaves the history untouched and
        // never disturbs the save that follows (every branch exits 0).
        var script =
            $"cd {dir} 2>/dev/null || exit 0; " +
            $"[ -f {file} ] || exit 0; " +
            $"mkdir -p {SshCommandExecutor.QuoteForShell(HistoryRelativeDir)} 2>/dev/null || exit 0; " +
            $"NEWEST=$(ls -1 {HistoryRelativeDir}/{glob} 2>/dev/null | sort | tail -n 1); " +
            $"if [ -n \"$NEWEST\" ] && cmp -s {file} \"$NEWEST\"; then exit 0; fi; " +
            $"cp -- {file} {HistoryRelativeDir}/{stamp}{Separator}{fileName} 2>/dev/null || exit 0; " +
            $"ls -1 {HistoryRelativeDir}/{glob} 2>/dev/null | sort -r | tail -n +{keepPlusOne} | " +
            "while read -r f; do rm -f -- \"$f\"; done; exit 0";

        try { await RunSshCommandAsync(ssh, script, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { /* best effort */ }
    }

    public async Task<ComposeHistoryListResult> ListOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, CancellationToken cancellationToken = default)
    {
        var dir = SshCommandExecutor.QuoteForShell(projectPath);
        var glob = "*" + Separator + SshCommandExecutor.QuoteForShell(fileName);

        var script =
            $"cd {dir}/{SshCommandExecutor.QuoteForShell(HistoryRelativeDir)} 2>/dev/null || exit 0; " +
            $"for f in {glob}; do [ -f \"$f\" ] || continue; printf '%s\\t%s\\n' \"$f\" \"$(wc -c < \"$f\")\"; done";

        SshCommandOutcome outcome;
        try { outcome = await RunSshCommandAsync(ssh, script, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeHistoryListResult(ComposeHistoryStatus.SshFailed, [],
                $"SSH to {ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        if (outcome.ExitStatus != 0)
            return new ComposeHistoryListResult(ComposeHistoryStatus.SshFailed, [],
                $"Listing the Compose history over SSH failed (exit {outcome.ExitStatus}).");

        var entries = new List<ComposeHistoryEntry>();
        foreach (var raw in outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            var id = tab < 0 ? line : line[..tab];
            var size = tab < 0 || !long.TryParse(line[(tab + 1)..].Trim(), out var s) ? 0 : s;
            if (TryParseStamp(id, fileName, out var savedUtc))
                entries.Add(new ComposeHistoryEntry(id, savedUtc, size));
        }
        entries.Sort((a, b) => b.SavedUtc.CompareTo(a.SavedUtc)); // newest first
        return ComposeHistoryListResult.Ok(entries);
    }

    public async Task<ComposeHistoryReadResult> ReadOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string entryId, CancellationToken cancellationToken = default)
    {
        if (!IsBareName(entryId))
            return new ComposeHistoryReadResult(ComposeHistoryStatus.NotFound, null, "Invalid history entry id.");

        var historyPath = SshCommandExecutor.QuoteForShell(
            $"{projectPath}/{HistoryRelativeDir}/{entryId}");
        var script = $"[ -f {historyPath} ] || exit 41; cat -- {historyPath}";

        SshCommandOutcome outcome;
        try { outcome = await RunSshCommandAsync(ssh, script, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ComposeHistoryReadResult(ComposeHistoryStatus.SshFailed, null,
                $"SSH to {ssh.Host}:{ssh.Port} failed: {ex.Message}");
        }

        return outcome.ExitStatus switch
        {
            0 => new ComposeHistoryReadResult(ComposeHistoryStatus.Ok, outcome.Stdout, null),
            41 => new ComposeHistoryReadResult(ComposeHistoryStatus.NotFound, null,
                $"History revision '{entryId}' was not found on the host."),
            _ => new ComposeHistoryReadResult(ComposeHistoryStatus.SshFailed, null,
                $"Reading the history revision over SSH failed (exit {outcome.ExitStatus})."),
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Entries for <paramref name="fileName"/>, oldest first (lexicographic
    /// stamp order). Size is left 0 here; <see cref="ListAsync"/> fills it.</summary>
    private static List<ComposeHistoryEntry> OrderedEntries(IReadOnlyList<string> names, string fileName)
    {
        var entries = new List<ComposeHistoryEntry>();
        foreach (var name in names)
            if (TryParseStamp(name, fileName, out var savedUtc))
                entries.Add(new ComposeHistoryEntry(name, savedUtc, 0));
        entries.Sort((a, b) => a.SavedUtc.CompareTo(b.SavedUtc)); // oldest first
        return entries;
    }

    private static bool TryParseStamp(string entryId, string fileName, out DateTime savedUtc)
    {
        savedUtc = default;
        var sep = entryId.IndexOf(Separator, StringComparison.Ordinal);
        if (sep < 0) return false;
        if (!string.Equals(entryId[(sep + Separator.Length)..], fileName, StringComparison.Ordinal)) return false;
        if (!DateTime.TryParseExact(entryId[..sep], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        savedUtc = parsed;
        return true;
    }

    private static bool IsBareName(string id) =>
        !string.IsNullOrEmpty(id) && !id.Contains('/') && !id.Contains('\\') && id is not "." and not ".."
        && !id.Contains("..", StringComparison.Ordinal);
}
