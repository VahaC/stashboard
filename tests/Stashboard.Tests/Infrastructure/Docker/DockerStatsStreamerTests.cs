using Docker.DotNet.Models;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V3.4 — covers <see cref="DockerStatsStreamer.ComputeSample"/>, the
/// per-snapshot transform that turns a raw <see cref="ContainerStatsResponse"/>
/// into the wire <see cref="DockerContainerStatsSample"/>. The streaming
/// pump itself is exercised by the controller tests via a mocked streamer.
/// </summary>
public class DockerStatsStreamerTests
{
    [Fact]
    public void ComputeSample_ReturnsNullCpu_OnFirstSample()
    {
        // First snapshot of a fresh stream — daemon ships PreCPUStats as
        // zero. With no baseline we can't compute a delta; the sample
        // should arrive with cpuPercent = null so the UI can render "—"
        // instead of a misleading 0%.
        var response = new ContainerStatsResponse
        {
            Read = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 100_000_000UL },
                SystemUsage = 1_000_000_000UL,
                OnlineCPUs = 4,
            },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Null(sample.CpuPercent);
        Assert.Equal(4, sample.OnlineCpus);
    }

    [Fact]
    public void ComputeSample_ComputesCpuPercent_FromCpuAndSystemDeltas()
    {
        // Classic docker stats formula:
        //   cpu_delta = 200ms of CPU time, system_delta = 1s of wall time,
        //   4 cores → 200/1000 * 4 * 100 = 80%
        var response = new ContainerStatsResponse
        {
            Read = new DateTime(2026, 5, 19, 12, 0, 1, DateTimeKind.Utc),
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 1_200_000_000UL },
                SystemUsage = 10_000_000_000UL,
                OnlineCPUs = 4,
            },
            PreCPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 1_000_000_000UL },
                SystemUsage = 9_000_000_000UL,
            },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.NotNull(sample.CpuPercent);
        Assert.Equal(80d, sample.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void ComputeSample_ReturnsNullCpu_WhenCountersGoBackwards()
    {
        // Daemon restart / clock drift / cgroup reset: the current TotalUsage
        // shows up smaller than the previous. Unsigned subtraction would
        // underflow into garbage; the streamer should report null instead.
        var response = new ContainerStatsResponse
        {
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 100UL },
                SystemUsage = 1_000UL,
                OnlineCPUs = 2,
            },
            PreCPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 5_000UL },
                SystemUsage = 10_000UL,
            },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Null(sample.CpuPercent);
    }

    [Fact]
    public void ComputeSample_FallsBackOnPercpuUsage_WhenOnlineCpusMissing()
    {
        // Older daemons (< 19.03) leave OnlineCPUs as 0 and the canonical
        // fallback is len(PercpuUsage). cpu_delta=1, system_delta=1, 8 cores
        // → 1/1 * 8 * 100 = 800.
        var response = new ContainerStatsResponse
        {
            CPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage
                {
                    TotalUsage = 2UL,
                    PercpuUsage = new ulong[] { 0, 0, 0, 0, 0, 0, 0, 0 },
                },
                SystemUsage = 2UL,
                OnlineCPUs = 0,
            },
            PreCPUStats = new CPUStats
            {
                CPUUsage = new CPUUsage { TotalUsage = 1UL },
                SystemUsage = 1UL,
            },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(8, sample.OnlineCpus);
        Assert.Equal(800d, sample.CpuPercent!.Value, precision: 6);
    }

    [Fact]
    public void ComputeSample_SubtractsKernelPageCache_FromMemoryUsage_Cgroupv1()
    {
        // cgroups v1: "cache" key carries the page cache value.
        var response = new ContainerStatsResponse
        {
            MemoryStats = new MemoryStats
            {
                Usage = 200UL * 1024 * 1024,   // 200 MB gross
                Limit = 1024UL * 1024 * 1024,  // 1 GB cap
                Stats = new Dictionary<string, ulong>
                {
                    { "cache", 50UL * 1024 * 1024 },  // 50 MB page cache
                },
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(150UL * 1024 * 1024, sample.MemoryUsageBytes);
        Assert.Equal(1024UL * 1024 * 1024, sample.MemoryLimitBytes);
        Assert.NotNull(sample.MemoryPercent);
        Assert.Equal(150d / 1024 * 100d, sample.MemoryPercent!.Value, precision: 6);
    }

    [Fact]
    public void ComputeSample_SubtractsKernelPageCache_FromMemoryUsage_Cgroupv2()
    {
        // cgroups v2 doesn't ship "cache" — "inactive_file" is the
        // equivalent the modern docker CLI subtracts.
        var response = new ContainerStatsResponse
        {
            MemoryStats = new MemoryStats
            {
                Usage = 100UL * 1024 * 1024,
                Limit = 512UL * 1024 * 1024,
                Stats = new Dictionary<string, ulong>
                {
                    { "inactive_file", 10UL * 1024 * 1024 },
                },
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(90UL * 1024 * 1024, sample.MemoryUsageBytes);
    }

    [Fact]
    public void ComputeSample_LeavesMemoryUsageIntact_WhenStatsDictMissing()
    {
        var response = new ContainerStatsResponse
        {
            MemoryStats = new MemoryStats
            {
                Usage = 100UL * 1024 * 1024,
                Limit = 512UL * 1024 * 1024,
                Stats = null!,
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(100UL * 1024 * 1024, sample.MemoryUsageBytes);
    }

    [Fact]
    public void ComputeSample_ReturnsNullMemoryPercent_WhenLimitIsZero()
    {
        // Unconstrained container (no `-m` / `mem_limit`): the daemon
        // reports Limit = 0. Dividing by zero would produce NaN — the
        // sample should signal "unknown" instead.
        var response = new ContainerStatsResponse
        {
            MemoryStats = new MemoryStats
            {
                Usage = 100UL * 1024 * 1024,
                Limit = 0UL,
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Null(sample.MemoryPercent);
    }

    [Fact]
    public void ComputeSample_SumsNetworkBytes_AcrossAllInterfaces()
    {
        var response = new ContainerStatsResponse
        {
            Networks = new Dictionary<string, NetworkStats>
            {
                { "eth0", new NetworkStats { RxBytes = 1000, TxBytes = 200 } },
                { "eth1", new NetworkStats { RxBytes = 50, TxBytes = 25 } },
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(1050UL, sample.NetworkRxBytes);
        Assert.Equal(225UL, sample.NetworkTxBytes);
    }

    [Fact]
    public void ComputeSample_SumsBlockIoBytes_IgnoringSyncAsyncTotalRollups()
    {
        // The daemon emits multiple Op kinds per device — "read" / "write"
        // are the breakdown; "sync" / "async" / "total" are rollups that
        // would double-count if we summed everything.
        var response = new ContainerStatsResponse
        {
            BlkioStats = new BlkioStats
            {
                IoServiceBytesRecursive = new[]
                {
                    new BlkioStatEntry { Op = "read", Value = 4096 },
                    new BlkioStatEntry { Op = "write", Value = 1024 },
                    new BlkioStatEntry { Op = "read", Value = 100 },  // second device
                    new BlkioStatEntry { Op = "sync", Value = 9999 },
                    new BlkioStatEntry { Op = "async", Value = 9999 },
                    new BlkioStatEntry { Op = "total", Value = 9999 },
                },
            },
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Equal(4196UL, sample.BlockReadBytes);
        Assert.Equal(1024UL, sample.BlockWriteBytes);
    }

    [Fact]
    public void ComputeSample_HandlesEmptyResponse_Gracefully()
    {
        // A snapshot from a freshly created container with no
        // network / block I/O activity yet shouldn't throw.
        var response = new ContainerStatsResponse
        {
            CPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            PreCPUStats = new CPUStats { CPUUsage = new CPUUsage() },
            MemoryStats = new MemoryStats(),
        };

        var sample = DockerStatsStreamer.ComputeSample(response);

        Assert.Null(sample.CpuPercent);
        Assert.Equal(0UL, sample.MemoryUsageBytes);
        Assert.Equal(0UL, sample.NetworkRxBytes);
        Assert.Equal(0UL, sample.BlockReadBytes);
        Assert.True(sample.OnlineCpus >= 1, "OnlineCpus should default to at least 1 to avoid divide-by-zero downstream.");
    }
}
