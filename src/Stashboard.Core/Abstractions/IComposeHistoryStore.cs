namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.6 — one kept revision of a project's Compose file under
/// <c>&lt;project&gt;/.stashboard/history/</c>. <see cref="Id"/> is the bare
/// history file name (the stamp + original file name) the restore/read endpoints
/// round-trip; it never contains a path separator.
/// </summary>
public sealed record ComposeHistoryEntry(string Id, DateTime SavedUtc, long SizeBytes);

/// <summary>
/// V7.6 — categorical outcome of a history read/list. Mirrors the
/// "never throw, branch on a typed status" contract the V7.0 reader uses.
/// </summary>
public enum ComposeHistoryStatus
{
    Ok = 0,

    /// <summary>The project (or its history) directory / the requested entry does
    /// not exist. Listing an empty history is still <see cref="Ok"/> with no
    /// entries — this is for a genuinely missing target.</summary>
    NotFound = 1,

    /// <summary>SSH transport failure (host unreachable, auth, timeout).</summary>
    SshFailed = 2,
}

/// <summary>V7.6 — result of <see cref="IComposeHistoryStore.ListAsync"/>.</summary>
public sealed record ComposeHistoryListResult(
    ComposeHistoryStatus Status, IReadOnlyList<ComposeHistoryEntry> Entries, string? Error)
{
    public static ComposeHistoryListResult Ok(IReadOnlyList<ComposeHistoryEntry> entries) =>
        new(ComposeHistoryStatus.Ok, entries, null);
}

/// <summary>V7.6 — result of <see cref="IComposeHistoryStore.ReadAsync"/>.
/// <see cref="Content"/> is set only when <see cref="Status"/> is
/// <see cref="ComposeHistoryStatus.Ok"/>.</summary>
public sealed record ComposeHistoryReadResult(
    ComposeHistoryStatus Status, string? Content, string? Error);

/// <summary>
/// V7.6 — keeps the last N revisions of a project's Compose file on disk next to
/// the file itself (<c>&lt;project&gt;/.stashboard/history/&lt;stamp&gt;__&lt;file&gt;</c>)
/// so a bad save can be rolled back with the Restore button. Two transports, same
/// shape: local (the V5.2 bind mount, LocalSocket connections) and over the
/// connection's SSH credentials (Ssh connections). Snapshotting is best-effort —
/// a history failure must never block the actual save — and de-duplicates against
/// the newest entry so repeated failed/no-op saves don't litter the history.
/// </summary>
public interface IComposeHistoryStore
{
    /// <summary>How many revisions are kept per file before the oldest is pruned.</summary>
    int KeepLast { get; }

    /// <summary>Snapshots the current on-disk <paramref name="fileName"/> into the
    /// project's history directory and prunes to <see cref="KeepLast"/>. No-op when
    /// the file does not yet exist or is byte-identical to the newest kept revision.
    /// Never throws.</summary>
    Task SnapshotAsync(string projectPath, string fileName, CancellationToken cancellationToken = default);

    /// <summary>SSH variant of <see cref="SnapshotAsync"/>.</summary>
    Task SnapshotOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Lists the kept revisions of <paramref name="fileName"/>, newest
    /// first. An absent history directory is <see cref="ComposeHistoryStatus.Ok"/>
    /// with no entries.</summary>
    Task<ComposeHistoryListResult> ListAsync(
        string projectPath, string fileName, CancellationToken cancellationToken = default);

    /// <summary>SSH variant of <see cref="ListAsync"/>.</summary>
    Task<ComposeHistoryListResult> ListOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Reads one kept revision by its <paramref name="entryId"/> (a bare
    /// history file name; rejected when it contains a path separator).</summary>
    Task<ComposeHistoryReadResult> ReadAsync(
        string projectPath, string entryId, CancellationToken cancellationToken = default);

    /// <summary>SSH variant of <see cref="ReadAsync"/>.</summary>
    Task<ComposeHistoryReadResult> ReadOverSshAsync(
        DockerSshCredentials ssh, string projectPath, string entryId, CancellationToken cancellationToken = default);
}
