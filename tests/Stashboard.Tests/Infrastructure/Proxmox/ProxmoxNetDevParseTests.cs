using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.1 — unit tests for the <c>/proc/net/dev</c> error parser that backs the
/// NIC-spike alert category. Only the rx/tx <em>error</em> columns are summed
/// (drops are excluded — benign on a bridged node); verifies loopback + virtual
/// interfaces are skipped and junk is tolerated.
/// </summary>
public class ProxmoxNetDevParseTests
{
    // Columns after "iface:": rx_bytes rx_packets rx_errs rx_drop rx_fifo
    // rx_frame rx_compressed rx_multicast tx_bytes tx_packets tx_errs tx_drop …
    private const string Sample = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo: 1000 10 5 5 0 0 0 0 1000 10 5 5 0 0 0 0
          eth0: 5000 50 2 3 0 0 0 0 6000 60 4 1 0 0 0 0
          eth1: 7000 70 0 0 0 0 0 0 8000 80 0 0 0 0 0 0
        """;

    [Fact]
    public void ParseNetDevErrors_SumsErrColumnsOnly_ExcludingDrops_AcrossPhysicalNics()
    {
        // eth0: rx_errs 2 + tx_errs 4 = 6 (rx_drop 3 / tx_drop 1 excluded). eth1: 0.
        // lo is excluded entirely.
        Assert.Equal(6, ProxmoxSshGuestInspector.ParseNetDevErrors(Sample));
    }

    [Fact]
    public void ParseNetDevErrors_SkipsVirtualInterfaces()
    {
        const string withVirtual = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
              eth0: 1 1 1 1 0 0 0 0 1 1 1 1 0 0 0 0
              veth9a0: 1 1 99 99 0 0 0 0 1 1 99 99 0 0 0 0
              fwbr101i0: 1 1 99 99 0 0 0 0 1 1 99 99 0 0 0 0
              docker0: 1 1 99 99 0 0 0 0 1 1 99 99 0 0 0 0
            """;
        // Only eth0's rx_errs 1 + tx_errs 1 = 2 counts; veth/fwbr/docker are guest/bridge plumbing.
        Assert.Equal(2, ProxmoxSshGuestInspector.ParseNetDevErrors(withVirtual));
    }

    [Fact]
    public void ParseNetDevErrors_ToleratesMalformedLines()
    {
        const string junk = """
            garbage with no colon
            eth0: too few columns
            eth0: 1 1 7 7 0 0 0 0 1 1 7 7 0 0 0 0
            """;
        // rx_errs 7 + tx_errs 7 = 14.
        Assert.Equal(14, ProxmoxSshGuestInspector.ParseNetDevErrors(junk));
    }

    [Fact]
    public void ParseNetDevErrors_Empty_IsZero() =>
        Assert.Equal(0, ProxmoxSshGuestInspector.ParseNetDevErrors(""));
}



