namespace Stashboard.Core.Abstractions;

// ── V3.4 — Live container stats ─────────────────────────────────────────────

/// <summary>
/// V3.4 — outcome of <see cref="IDockerStatsStreamer.StreamStatsAsync"/>.
/// Mirrors the result-style envelope used by V3.3 logs so the controller
/// can branch on a single status field.
/// </summary>
public sealed record DockerStatsStreamResult(
    DockerHostStatus Status,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok;

    public static DockerStatsStreamResult Ok { get; } = new(DockerHostStatus.Ok, null);
}

/// <summary>
/// V3.4 — request parameters for a stats stream. <c>Stream = true</c>
/// follows the container's stats indefinitely (one sample per second from
/// the daemon) until the caller cancels. <c>Stream = false</c> + <c>OneShot
/// = true</c> returns exactly one snapshot without any deltas — fast, but
/// CPU% is always <c>null</c> because there is no PreCPUStats baseline.
/// </summary>
public sealed record DockerStatsStreamRequest(
    bool Stream = true,
    bool OneShot = false);

/// <summary>
/// V3.4 — single computed stats sample. Counters are flattened from
/// Docker's raw structure: network bytes are summed across every interface,
/// block I/O bytes are summed from <c>IoServiceBytesRecursive</c> rows. CPU
/// percentage is computed from the <c>(cpu_delta / system_delta) *
/// online_cpus</c> formula <c>docker stats</c> uses; the very first sample
/// in a stream has no PreCPUStats baseline and <see cref="CpuPercent"/>
/// arrives as <c>null</c>.
/// <para>
/// Memory <see cref="MemoryUsageBytes"/> already has the kernel page cache
/// subtracted (matching <c>docker stats</c>) — the daemon reports the gross
/// number which over-counts memory pressure for workloads with large I/O
/// buffers.
/// </para>
/// </summary>
public sealed record DockerContainerStatsSample(
    DateTime TimestampUtc,
    double? CpuPercent,
    ulong MemoryUsageBytes,
    ulong MemoryLimitBytes,
    double? MemoryPercent,
    ulong NetworkRxBytes,
    ulong NetworkTxBytes,
    ulong BlockReadBytes,
    ulong BlockWriteBytes,
    int OnlineCpus);

/// <summary>
/// V3.4 — streams per-second CPU / memory / network / block-I/O counters
/// from a Docker daemon. Read-only — never mutates the daemon.
/// Implementations must honour the cancellation token promptly so the HTTP
/// response can disconnect cleanly when the browser tab closes.
/// </summary>
public interface IDockerStatsStreamer
{
    /// <summary>
    /// Streams stats for <paramref name="containerName"/> on the daemon
    /// described by <paramref name="transport"/>. The streamer invokes
    /// <paramref name="onSample"/> for every computed sample; if the
    /// callback throws or the token is cancelled, the underlying Docker
    /// stream is disposed and the call returns.
    /// </summary>
    Task<DockerStatsStreamResult> StreamStatsAsync(
        DockerHostTransport transport,
        string containerName,
        DockerStatsStreamRequest request,
        Func<DockerContainerStatsSample, CancellationToken, Task> onSample,
        CancellationToken cancellationToken = default);
}
