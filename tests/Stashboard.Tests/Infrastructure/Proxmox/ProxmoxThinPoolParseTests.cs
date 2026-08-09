using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.2 — unit tests for the LVM-thin-pool collector: the pure
/// <c>lvs --reportformat json</c> parser (thin pools only, data/meta percents)
/// and the no-SSH "not available" path.
/// </summary>
public class ProxmoxThinPoolParseTests
{
    private const string LvsJson = """
        {
          "report": [
            {
              "lv": [
                {"lv_name":"data","vg_name":"pve","lv_size":"1000000000","data_percent":"82.50","metadata_percent":"10.20","lv_attr":"twi-aotz--"},
                {"lv_name":"root","vg_name":"pve","lv_size":"500000000","data_percent":"","metadata_percent":"","lv_attr":"-wi-ao----"},
                {"lv_name":"swap","vg_name":"pve","lv_size":"8000000","data_percent":"","metadata_percent":"","lv_attr":"-wi-ao----"}
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ParseThinPools_ReturnsOnlyThinPools_WithPercents()
    {
        var pools = ProxmoxSshGuestInspector.ParseThinPools(LvsJson);

        var pool = Assert.Single(pools);
        Assert.Equal("data", pool.Name);
        Assert.Equal("pve", pool.VolumeGroup);
        Assert.Equal(1_000_000_000, pool.SizeBytes);
        Assert.Equal(82.50, pool.DataPercent!.Value, 2);
        Assert.Equal(10.20, pool.MetadataPercent!.Value, 2);
    }

    [Fact]
    public void ParseThinPools_NoReport_IsEmpty()
    {
        Assert.Empty(ProxmoxSshGuestInspector.ParseThinPools("{}"));
    }

    [Fact]
    public async Task ReadNodeThinPools_NoSsh_IsNotAvailable()
    {
        var inspector = new ProxmoxSshGuestInspector(NullLogger<ProxmoxSshGuestInspector>.Instance);
        var result = await inspector.ReadNodeThinPoolsAsync(NoSshProfile());
        Assert.False(result.Available);
        Assert.Empty(result.Pools);
    }

    private static ProxmoxConnectionProfile NoSshProfile() =>
        new("https://pve.lan:8006", "pve", "root@pam!t", "secret", SkipTlsVerify: true, Ssh: null);
}



