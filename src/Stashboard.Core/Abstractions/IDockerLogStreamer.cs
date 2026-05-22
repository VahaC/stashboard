namespace Stashboard.Core.Abstractions;

// ── V3.3 — Real-time container logs ─────────────────────────────────────────

/// <summary>
/// V3.3 — outcome of <see cref="IDockerLogStreamer.StreamLogsAsync"/>. Mirrors
/// the result-style envelope used elsewhere on the host client so the
/// controller can branch on a single status field.
/// </summary>
public sealed record DockerLogStreamResult(
    DockerHostStatus Status,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok;

    public static DockerLogStreamResult Ok { get; } = new(DockerHostStatus.Ok, null);
}

/// <summary>
/// V3.3 — request parameters for a single log stream call. Mirrors the
/// subset of the Engine's <c>/containers/{id}/logs</c> query parameters we
/// expose to the UI. <c>Follow = false</c> performs a one-shot fetch (used
/// by the "Download" button); <c>Follow = true</c> tails the container
/// until the caller cancels.
/// </summary>
/// <param name="Tail">Number of trailing lines to ship as the initial
/// backlog. <c>null</c> ships everything the daemon still has on disk.</param>
/// <param name="Since">Lower bound for log entries — only lines emitted at
/// or after this Unix timestamp are returned. <c>null</c> imposes no
/// lower bound (i.e. ship from the start of the container's history).</param>
/// <param name="Follow">When <c>true</c>, the call blocks until the caller
/// cancels and streams new lines as they appear; when <c>false</c>, the call
/// returns once the existing log is drained.</param>
/// <param name="Timestamps">Ask the daemon to prepend an RFC3339Nano
/// timestamp to each line. We always parse it back out and surface it via
/// <see cref="DockerLogLine.TimestampUtc"/>, so the wire payload never
/// double-prints it.</param>
/// <param name="IncludeStdout">Include stdout frames. At least one of
/// stdout / stderr must be true; the streamer rejects a call with neither.</param>
/// <param name="IncludeStderr">Include stderr frames.</param>
public sealed record DockerLogStreamRequest(
    int? Tail,
    DateTimeOffset? Since,
    bool Follow,
    bool Timestamps = true,
    bool IncludeStdout = true,
    bool IncludeStderr = true);

/// <summary>
/// V3.3 — a single decoded log line. The <c>Stream</c> field tells the
/// frontend which colour / channel to render the line on; <c>Message</c> is
/// the line's text content with its trailing newline stripped and the
/// per-line timestamp prefix (when present) parsed out into
/// <c>TimestampUtc</c>. For container logs from a tty-attached container
/// every frame arrives on <see cref="DockerLogStreamChannel.Stdout"/> — the
/// daemon doesn't multiplex stdout / stderr in tty mode.
/// </summary>
public sealed record DockerLogLine(
    DockerLogStreamChannel Stream,
    DateTime? TimestampUtc,
    string Message);

/// <summary>V3.3 — which Docker channel a log line came from.</summary>
public enum DockerLogStreamChannel
{
    Stdout = 1,
    Stderr = 2,
}

/// <summary>
/// V3.3 — streams stdcopy-multiplexed log frames from a Docker daemon and
/// demuxes them into <see cref="DockerLogLine"/>s. Read-only — never writes
/// to stdin. Implementations must honour the cancellation token promptly so
/// the SSE / chunked HTTP response can disconnect cleanly when the browser
/// tab closes.
/// </summary>
public interface IDockerLogStreamer
{
    /// <summary>
    /// Streams logs for <paramref name="containerName"/> on the daemon
    /// described by <paramref name="transport"/>. The streamer invokes
    /// <paramref name="onLine"/> for every decoded log line; if the callback
    /// throws or the token is cancelled, the underlying Docker stream is
    /// disposed and the call returns.
    /// </summary>
    Task<DockerLogStreamResult> StreamLogsAsync(
        DockerHostTransport transport,
        string containerName,
        DockerLogStreamRequest request,
        Func<DockerLogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken = default);
}
