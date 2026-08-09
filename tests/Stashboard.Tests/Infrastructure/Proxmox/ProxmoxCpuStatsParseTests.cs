using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.2 — unit tests for the CPU-stats collector: the pure <c>/proc/stat</c>
/// two-sample diff (per-core util + steal, aggregate steal) and the
/// <c>/proc/meminfo</c> MemAvailable parser, plus the capability-absent path
/// (no SSH → "not available", never an exception).
/// </summary>
public class ProxmoxCpuStatsParseTests
{
    // cpu0: 50% busy, no steal. cpu1: 50% busy, 50% steal. aggregate: 25% steal.
    private const string Stat1 = """
        cpu  0 0 0 200 0 0 0 0 0 0
        cpu0 0 0 0 100 0 0 0 0 0 0
        cpu1 0 0 0 100 0 0 0 0 0 0
        intr 12345
        """;
    private const string Stat2 = """
        cpu  50 0 0 300 0 0 0 50 0 0
        cpu0 50 0 0 150 0 0 0 0 0 0
        cpu1 0 0 0 150 0 0 0 50 0 0
        intr 99999
        """;

    [Fact]
    public void ParseCpuStats_ComputesPerCoreUtilAndSteal()
    {
        var (cores, steal) = ProxmoxSshGuestInspector.ParseCpuStats(Stat1, Stat2);

        Assert.Equal(2, cores.Count);
        var core0 = cores.Single(c => c.Core == 0);
        Assert.Equal(50.0, core0.UtilPercent, 1);
        Assert.Equal(0.0, core0.StealPercent, 1);

        var core1 = cores.Single(c => c.Core == 1);
        Assert.Equal(50.0, core1.UtilPercent, 1);
        Assert.Equal(50.0, core1.StealPercent, 1);

        // Aggregate steal comes from the bare "cpu" line, not a per-core average.
        Assert.NotNull(steal);
        Assert.Equal(25.0, steal!.Value, 1);
    }

    [Fact]
    public void ParseCpuStats_NoDelta_IsZeroNotNaN()
    {
        var (cores, _) = ProxmoxSshGuestInspector.ParseCpuStats(Stat1, Stat1);
        Assert.All(cores, c => Assert.Equal(0.0, c.UtilPercent));
    }

    // V7.2.1 — an idle 4-core host (only the idle column advances between the two
    // samples) reports every core at exactly 0% — fully populated, not empty. This
    // pins down that "#0..#3 all 0%" on a quiet PBS node is genuine idle, not a
    // parse failure: the per-core grid still renders four cores, each at 0%.
    [Fact]
    public void ParseCpuStats_IdleHost_AllCoresZero_StillReportsEveryCore()
    {
        const string idle1 = """
            cpu  0 0 0 400 0 0 0 0 0 0
            cpu0 0 0 0 100 0 0 0 0 0 0
            cpu1 0 0 0 100 0 0 0 0 0 0
            cpu2 0 0 0 100 0 0 0 0 0 0
            cpu3 0 0 0 100 0 0 0 0 0 0
            """;
        const string idle2 = """
            cpu  0 0 0 800 0 0 0 0 0 0
            cpu0 0 0 0 200 0 0 0 0 0 0
            cpu1 0 0 0 200 0 0 0 0 0 0
            cpu2 0 0 0 200 0 0 0 0 0 0
            cpu3 0 0 0 200 0 0 0 0 0 0
            """;

        var (cores, steal) = ProxmoxSshGuestInspector.ParseCpuStats(idle1, idle2);

        Assert.Equal(4, cores.Count);                       // all four cores present
        Assert.Equal(new[] { 0, 1, 2, 3 }, cores.Select(c => c.Core));
        Assert.All(cores, c => Assert.Equal(0.0, c.UtilPercent, 1));
        Assert.Equal(0.0, steal!.Value, 1);                 // idle ⇒ no steal
    }

    [Fact]
    public void ParseMemAvailable_ReadsKbAndConvertsToBytes()
    {
        const string meminfo = """
            MemTotal:       16384000 kB
            MemFree:         1000000 kB
            MemAvailable:    8192000 kB
            Buffers:          200000 kB
            """;
        Assert.Equal(8192000L * 1024, ProxmoxSshGuestInspector.ParseMemAvailable(meminfo));
    }

    [Fact]
    public void ParseMemAvailable_Missing_IsNull()
    {
        Assert.Null(ProxmoxSshGuestInspector.ParseMemAvailable("MemTotal: 100 kB\nMemFree: 50 kB"));
    }

    [Fact]
    public async Task ReadNodeCpuStats_NoSsh_IsNotAvailable()
    {
        var inspector = new ProxmoxSshGuestInspector(NullLogger<ProxmoxSshGuestInspector>.Instance);
        var result = await inspector.ReadNodeCpuStatsAsync(NoSshProfile());

        Assert.False(result.Available);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Cores);
    }

    private static ProxmoxConnectionProfile NoSshProfile() =>
        new("https://pve.lan:8006", "pve", "root@pam!t", "secret", SkipTlsVerify: true, Ssh: null);
}



