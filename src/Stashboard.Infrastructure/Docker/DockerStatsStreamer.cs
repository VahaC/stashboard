using System.Net;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V3.4 — production <see cref="IDockerStatsStreamer"/>. Wraps Docker's
/// <c>GET /containers/{id}/stats?stream=true</c> endpoint, which the daemon
/// emits one JSON snapshot per second. Each snapshot is transformed into a
/// flattened <see cref="DockerContainerStatsSample"/> with computed CPU /
/// memory percentages and summed network / block-I/O counters so the
/// frontend can render straight from the wire payload without re-doing the
/// math browser-side.
/// </summary>
/// <remarks>
/// Docker.DotNet's stats API is push-based — it hands us an
/// <see cref="IProgress{T}"/> callback that fires from inside its stream
/// reader. Our consumer (the HTTP response writer) is async, so we
/// decouple the two with a small bounded <see cref="Channel{T}"/>:
/// <list type="bullet">
///   <item><c>Progress.Report</c> writes raw responses into the channel.</item>
///   <item>A consumer task reads responses, transforms them into samples,
///         and awaits the user callback (which writes NDJSON to the
///         response body).</item>
/// </list>
/// The channel uses <c>DropOldest</c> so a slow consumer can never back-
/// pressure the Docker connection — the user sees the latest snapshot
/// instead of stale ones piling up.
/// </remarks>
public sealed class DockerStatsStreamer(
    IDockerClientFactory dockerClientFactory,
    ILogger<DockerStatsStreamer> logger) : IDockerStatsStreamer
{
    public async Task<DockerStatsStreamResult> StreamStatsAsync(
        DockerHostTransport transport,
        string containerName,
        DockerStatsStreamRequest request,
        Func<DockerContainerStatsSample, CancellationToken, Task> onSample,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return new DockerStatsStreamResult(DockerHostStatus.ContainerNotFound, "Container name is required.");

        IDockerClient client;
        try
        {
            client = dockerClientFactory.Create(transport.HostType, transport.HostUrl, transport.Tls, transport.Ssh);
        }
        catch (NotSupportedException ex)
        {
            return new DockerStatsStreamResult(DockerHostStatus.UnsupportedHostType, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed for Docker host: {Host}", transport.Ssh?.Host);
            return new DockerStatsStreamResult(DockerHostStatus.HostUnreachable, $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            // Bounded channel with DropOldest so a slow HTTP consumer never
            // back-pressures the Docker socket. Capacity 4 is enough to
            // smooth out a one-frame stall without queueing stale data.
            var channel = Channel.CreateBounded<ContainerStatsResponse>(
                new BoundedChannelOptions(4)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });

            var progress = new Progress<ContainerStatsResponse>(response =>
            {
                // Channel.Writer.TryWrite never blocks; DropOldest evicts
                // stale samples to make room for the newest one.
                channel.Writer.TryWrite(response);
            });

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var parameters = new ContainerStatsParameters
            {
                Stream = request.Stream,
                OneShot = request.OneShot,
            };

            // Run the Docker.DotNet pump as a background task; complete the
            // channel writer when the pump finishes so the consumer below
            // exits cleanly.
            var pumpTask = Task.Run(async () =>
            {
                try
                {
                    await client.Containers.GetContainerStatsAsync(
                        containerName, parameters, progress, linked.Token);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, linked.Token);

            try
            {
                await foreach (var response in channel.Reader.ReadAllAsync(linked.Token))
                {
                    var sample = ComputeSample(response);
                    await onSample(sample, linked.Token);
                }

                // Surface any terminal failure from the pump so the
                // controller can write an error frame on the way out.
                await pumpTask;
                return DockerStatsStreamResult.Ok;
            }
            catch (DockerContainerNotFoundException)
            {
                linked.Cancel();
                await SuppressAsync(pumpTask);
                return new DockerStatsStreamResult(DockerHostStatus.ContainerNotFound,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                linked.Cancel();
                await SuppressAsync(pumpTask);
                return new DockerStatsStreamResult(DockerHostStatus.ContainerNotFound,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (HttpRequestException ex)
            {
                linked.Cancel();
                await SuppressAsync(pumpTask);
                logger.LogWarning(ex, "Docker host unreachable when streaming stats for {Container}", containerName);
                return new DockerStatsStreamResult(DockerHostStatus.HostUnreachable, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SuppressAsync(pumpTask);
                return DockerStatsStreamResult.Ok;
            }
            catch (IOException ex)
            {
                linked.Cancel();
                await SuppressAsync(pumpTask);
                logger.LogDebug(ex, "Docker stats stream for {Container} ended unexpectedly", containerName);
                return DockerStatsStreamResult.Ok;
            }
        }
    }

    /// <summary>
    /// Transforms one raw <see cref="ContainerStatsResponse"/> snapshot
    /// into a flattened <see cref="DockerContainerStatsSample"/>.
    /// Implements the same CPU% formula as <c>docker stats</c>:
    /// <code>cpu% = (cpu_delta / system_delta) * online_cpus * 100</code>
    /// and the same memory adjustment (subtracting the kernel page cache
    /// to match what users see in the CLI).
    /// </summary>
    internal static DockerContainerStatsSample ComputeSample(ContainerStatsResponse response)
    {
        var onlineCpus = ResolveOnlineCpus(response);
        var cpuPercent = ComputeCpuPercent(response, onlineCpus);
        var (memoryUsage, memoryLimit, memoryPercent) = ComputeMemory(response);
        var (rxBytes, txBytes) = SumNetwork(response);
        var (readBytes, writeBytes) = SumBlockIo(response);

        return new DockerContainerStatsSample(
            TimestampUtc: response.Read == default ? DateTime.UtcNow : response.Read.ToUniversalTime(),
            CpuPercent: cpuPercent,
            MemoryUsageBytes: memoryUsage,
            MemoryLimitBytes: memoryLimit,
            MemoryPercent: memoryPercent,
            NetworkRxBytes: rxBytes,
            NetworkTxBytes: txBytes,
            BlockReadBytes: readBytes,
            BlockWriteBytes: writeBytes,
            OnlineCpus: onlineCpus);
    }

    private static int ResolveOnlineCpus(ContainerStatsResponse response)
    {
        if (response.CPUStats?.OnlineCPUs > 0) return (int)response.CPUStats.OnlineCPUs;
        // Older daemons (Docker < 19.03) don't populate OnlineCPUs and the
        // canonical fallback is len(PercpuUsage). Defensive max(1) keeps
        // CPU% from being multiplied by zero on minimal stub responses.
        var percpu = response.CPUStats?.CPUUsage?.PercpuUsage?.Count ?? 0;
        return Math.Max(1, percpu);
    }

    private static double? ComputeCpuPercent(ContainerStatsResponse response, int onlineCpus)
    {
        var cpu = response.CPUStats?.CPUUsage?.TotalUsage ?? 0UL;
        var preCpu = response.PreCPUStats?.CPUUsage?.TotalUsage ?? 0UL;
        var system = response.CPUStats?.SystemUsage ?? 0UL;
        var preSystem = response.PreCPUStats?.SystemUsage ?? 0UL;

        // No baseline → first sample of a fresh stream. Report null so the
        // UI can render "—" instead of a misleading 0%.
        if (preCpu == 0 && preSystem == 0) return null;

        // Unsigned subtraction with explicit guard — clock drift or daemon
        // restarts can produce snapshots where pre > current.
        if (cpu < preCpu || system < preSystem) return null;

        var cpuDelta = cpu - preCpu;
        var systemDelta = system - preSystem;
        if (systemDelta == 0 || cpuDelta == 0) return 0d;

        return (double)cpuDelta / systemDelta * onlineCpus * 100d;
    }

    private static (ulong Usage, ulong Limit, double? Percent) ComputeMemory(ContainerStatsResponse response)
    {
        var rawUsage = response.MemoryStats?.Usage ?? 0UL;
        var limit = response.MemoryStats?.Limit ?? 0UL;

        // `docker stats` subtracts the kernel page cache so the number
        // reflects RSS pressure rather than gross usage. cgroups v1 ships
        // "cache"; cgroups v2 ships "inactive_file" (and only that). Either
        // / both may be missing — fall through to the raw usage.
        var stats = response.MemoryStats?.Stats;
        var cache = 0UL;
        if (stats is not null)
        {
            if (stats.TryGetValue("inactive_file", out var inactiveFile))
                cache = inactiveFile;
            else if (stats.TryGetValue("cache", out var cacheValue))
                cache = cacheValue;
            else if (stats.TryGetValue("total_inactive_file", out var totalInactive))
                cache = totalInactive;
        }
        var usage = cache <= rawUsage ? rawUsage - cache : rawUsage;

        double? percent = null;
        if (limit > 0)
            percent = (double)usage / limit * 100d;

        return (usage, limit, percent);
    }

    private static (ulong Rx, ulong Tx) SumNetwork(ContainerStatsResponse response)
    {
        if (response.Networks is null || response.Networks.Count == 0)
            return (0UL, 0UL);

        var rx = 0UL;
        var tx = 0UL;
        foreach (var iface in response.Networks.Values)
        {
            if (iface is null) continue;
            rx += iface.RxBytes;
            tx += iface.TxBytes;
        }
        return (rx, tx);
    }

    private static (ulong Read, ulong Write) SumBlockIo(ContainerStatsResponse response)
    {
        var entries = response.BlkioStats?.IoServiceBytesRecursive;
        if (entries is null || entries.Count == 0) return (0UL, 0UL);

        var read = 0UL;
        var write = 0UL;
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Op)) continue;
            // Daemon reports "read" / "write" (lowercase) in modern
            // versions; legacy ones use "Read" / "Write". Treat
            // case-insensitively, ignore "sync" / "async" / "total"
            // rollups so we don't double-count.
            if (entry.Op.Equals("read", StringComparison.OrdinalIgnoreCase))
                read += entry.Value;
            else if (entry.Op.Equals("write", StringComparison.OrdinalIgnoreCase))
                write += entry.Value;
        }
        return (read, write);
    }

    private static async Task SuppressAsync(Task task)
    {
        try { await task; }
        catch { /* swallow — original error already routed to caller */ }
    }

    private static bool IsSshConnectFailure(Exception ex) =>
        ex is SshException or System.Net.Sockets.SocketException;
}
