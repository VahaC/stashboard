using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.2 — unit tests for the per-interface collector: the pure
/// <c>/proc/net/dev</c> two-sample diff (RX/TX rates + error/drop counters)
/// joined with the <c>/sys/class/net</c> link section, virtual-interface
/// filtering, and the no-SSH "not available" path.
/// </summary>
public class ProxmoxInterfaceStatsParseTests
{
    // After "iface:": rx_bytes rx_packets rx_errs rx_drop fifo frame compressed
    // multicast tx_bytes tx_packets tx_errs tx_drop …
    private const string Dev1 = """
        Inter-|   Receive                                                |  Transmit
         face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed
            lo: 1 1 0 0 0 0 0 0 1 1 0 0 0 0 0 0
          eth0: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0
        veth9: 1 1 0 0 0 0 0 0 1 1 0 0 0 0 0 0
        """;
    private const string Dev2 = """
        Inter-|   Receive                                                |  Transmit
         face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed
            lo: 1 1 0 0 0 0 0 0 1 1 0 0 0 0 0 0
          eth0: 3000 30 5 2 0 0 0 0 5000 50 3 1 0 0 0 0
        veth9: 99 99 9 9 0 0 0 0 99 99 9 9 0 0 0 0
        """;
    private const string Links = """
        eth0 up 1000 full
        lo unknown -1 unknown
        veth9 up 10000 full
        """;

    [Fact]
    public void ParseInterfaceStats_ComputesRatesErrorsAndLink_ForPhysicalNics()
    {
        var ifaces = ProxmoxSshGuestInspector.ParseInterfaceStats(Dev1, Dev2, dt: 1.0, Links);

        var eth0 = ifaces.Single(i => i.Iface == "eth0");
        Assert.Equal(2000, eth0.RxBytesPerSec, 0);  // (3000-1000)/1s
        Assert.Equal(3000, eth0.TxBytesPerSec, 0);  // (5000-2000)/1s
        Assert.Equal(5, eth0.RxErrors);             // cumulative at second sample
        Assert.Equal(3, eth0.TxErrors);
        Assert.Equal(2, eth0.RxDropped);
        Assert.Equal(1, eth0.TxDropped);
        Assert.Equal(1000, eth0.SpeedMbps);
        Assert.Equal("full", eth0.Duplex);
        Assert.Equal("up", eth0.OperState);
    }

    [Fact]
    public void ParseInterfaceStats_SkipsLoopbackAndVirtual()
    {
        var ifaces = ProxmoxSshGuestInspector.ParseInterfaceStats(Dev1, Dev2, 1.0, Links);
        Assert.DoesNotContain(ifaces, i => i.Iface == "lo");
        Assert.DoesNotContain(ifaces, i => i.Iface == "veth9");
        Assert.Single(ifaces);
    }

    [Fact]
    public void ParseInterfaceStats_DownLink_HasNoSpeed()
    {
        // /sys reports -1 for a down link → speed parsed as null, not -1.
        const string links = "eth0 down -1 unknown";
        var eth0 = ProxmoxSshGuestInspector.ParseInterfaceStats(Dev1, Dev2, 1.0, links).Single();
        Assert.Null(eth0.SpeedMbps);
        Assert.Null(eth0.Duplex);
        Assert.Equal("down", eth0.OperState);
    }

    [Fact]
    public async Task ReadNodeInterfaceStats_NoSsh_IsNotAvailable()
    {
        var inspector = new ProxmoxSshGuestInspector(NullLogger<ProxmoxSshGuestInspector>.Instance);
        var result = await inspector.ReadNodeInterfaceStatsAsync(NoSshProfile());
        Assert.False(result.Available);
        Assert.Empty(result.Interfaces);
    }

    private static ProxmoxConnectionProfile NoSshProfile() =>
        new("https://pve.lan:8006", "pve", "root@pam!t", "secret", SkipTlsVerify: true, Ssh: null);
}



