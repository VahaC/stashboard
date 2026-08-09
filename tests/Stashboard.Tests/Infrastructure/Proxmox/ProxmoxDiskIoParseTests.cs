using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.2 — unit tests for the disk-IO collector: the pure <c>/proc/diskstats</c>
/// two-sample diff (throughput / IOPS / await), partition + virtual-device
/// filtering, and the no-SSH "not available" path.
/// </summary>
public class ProxmoxDiskIoParseTests
{
    // Columns: major minor name reads rmerged sectors_read ms_read writes wmerged
    //          sectors_written ms_write io_in_progress …
    private const string Stats1 = """
           8       0 sda 100 0 2000 500 50 0 1000 200 0 0 0
           8       1 sda1 100 0 2000 500 50 0 1000 200 0 0 0
         259       0 nvme0n1 10 0 80 30 5 0 40 10 0 0 0
           7       0 loop0 999 0 9999 999 999 0 9999 999 0 0 0
        """;
    private const string Stats2 = """
           8       0 sda 200 0 4000 600 100 0 3000 400 0 0 0
           8       1 sda1 200 0 4000 600 100 0 3000 400 0 0 0
         259       0 nvme0n1 10 0 80 30 5 0 40 10 0 0 0
           7       0 loop0 1999 0 19999 1999 1999 0 19999 1999 0 0 0
        """;

    [Fact]
    public void ParseDiskIo_ComputesThroughputIopsAndAwait_ForWholeDisks()
    {
        var disks = ProxmoxSshGuestInspector.ParseDiskIo(Stats1, Stats2, dt: 1.0);

        var sda = disks.Single(d => d.Device == "sda");
        // sectors Δ: read 2000, written 2000 → ×512 B = 1,024,000 B/s each.
        Assert.Equal(1_024_000, sda.ReadBytesPerSec, 0);
        Assert.Equal(1_024_000, sda.WriteBytesPerSec, 0);
        // ops Δ: 100 reads, 50 writes over 1s.
        Assert.Equal(100, sda.ReadIops, 0);
        Assert.Equal(50, sda.WriteIops, 0);
        // await: ms Δ / ops → (600-500)/100 = 1ms read, (400-200)/50 = 4ms write.
        Assert.Equal(1.0, sda.ReadAwaitMs!.Value, 2);
        Assert.Equal(4.0, sda.WriteAwaitMs!.Value, 2);
    }

    [Fact]
    public void ParseDiskIo_SkipsPartitionsLoopsAndIdleDisks()
    {
        var disks = ProxmoxSshGuestInspector.ParseDiskIo(Stats1, Stats2, dt: 1.0);

        Assert.DoesNotContain(disks, d => d.Device == "sda1");    // partition
        Assert.DoesNotContain(disks, d => d.Device == "loop0");   // virtual
        Assert.DoesNotContain(disks, d => d.Device == "nvme0n1"); // zero delta → idle, dropped
        Assert.Single(disks);
    }

    [Fact]
    public void ParseDiskIo_NoAwaitWhenNoOps()
    {
        // A disk that only wrote: read await is null (no read ops to average).
        const string a = "8 0 sda 0 0 0 0 10 0 100 50 0 0 0";
        const string b = "8 0 sda 0 0 0 0 20 0 300 90 0 0 0";
        var sda = ProxmoxSshGuestInspector.ParseDiskIo(a, b, 1.0).Single();
        Assert.Null(sda.ReadAwaitMs);
        Assert.Equal(4.0, sda.WriteAwaitMs!.Value, 2); // (90-50)/10
    }

    [Fact]
    public async Task ReadNodeDiskIo_NoSsh_IsNotAvailable()
    {
        var inspector = new ProxmoxSshGuestInspector(NullLogger<ProxmoxSshGuestInspector>.Instance);
        var result = await inspector.ReadNodeDiskIoAsync(NoSshProfile());
        Assert.False(result.Available);
        Assert.Empty(result.Disks);
    }

    private static ProxmoxConnectionProfile NoSshProfile() =>
        new("https://pve.lan:8006", "pve", "root@pam!t", "secret", SkipTlsVerify: true, Ssh: null);
}



