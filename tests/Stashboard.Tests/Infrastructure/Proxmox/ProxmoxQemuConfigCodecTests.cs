using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V8.5 — round-trip + formatting tests for <see cref="ProxmoxQemuConfigCodec"/>, the
/// layer that turns a VM's compound <c>net&lt;n&gt;</c> and disk option lines into the
/// structured edit models and back. A VM NIC differs from an LXC one — the first token
/// is the device model carrying the MAC as its value — so these guard that the model /
/// MAC split and the safe-flag disk re-emit never drop or invent an option.
/// </summary>
public class ProxmoxQemuConfigCodecTests
{
    // ── net<n> ────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseNet_SplitsModelAndMac()
    {
        var c = ProxmoxQemuConfigCodec.ParseNet("net0", "virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,tag=10,firewall=1");

        Assert.Equal("virtio", c.Model);
        Assert.Equal("AA:BB:CC:DD:EE:FF", c.MacAddr);
        Assert.Equal("vmbr0", c.Bridge);
        Assert.Equal(10, c.Tag);
        Assert.True(c.Firewall);
    }

    [Fact]
    public void ParseNet_ModelWithoutMac()
    {
        var c = ProxmoxQemuConfigCodec.ParseNet("net1", "e1000,bridge=vmbr1");

        Assert.Equal("e1000", c.Model);
        Assert.Null(c.MacAddr);
        Assert.Equal("vmbr1", c.Bridge);
    }

    [Fact]
    public void FormatNet_EmitsModelLeadingWithMac()
    {
        var line = ProxmoxQemuConfigCodec.FormatNet(new(
            "net0", Model: "virtio", MacAddr: "AA:BB:CC:DD:EE:FF", Bridge: "vmbr0", Tag: 10, Firewall: true));

        Assert.Equal("virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,tag=10,firewall=1", line);
    }

    [Fact]
    public void FormatNet_DefaultsModelToVirtio()
    {
        var line = ProxmoxQemuConfigCodec.FormatNet(new("net0", Bridge: "vmbr0"));
        Assert.Equal("virtio,bridge=vmbr0", line);
    }

    [Fact]
    public void Net_RoundTrips_PreservingUnknownOptions()
    {
        const string raw = "vmxnet3=AA:BB:CC:DD:EE:01,bridge=vmbr0,tag=5,mtu=9000,queues=4,trunks=2;3";
        var parsed = ProxmoxQemuConfigCodec.ParseNet("net0", raw);

        // trunks is unmodelled → preserved verbatim in Extra.
        Assert.Equal("trunks=2;3", parsed.Extra);
        var reformatted = ProxmoxQemuConfigCodec.FormatNet(parsed);
        Assert.Equal(Set(raw), Set(reformatted));   // option set preserved (order-agnostic)
    }

    [Fact]
    public void FormatNet_RawWinsVerbatim()
    {
        var line = ProxmoxQemuConfigCodec.FormatNet(new("net0", Raw: "virtio,bridge=vmbr9,link_down=1", Model: "ignored"));
        Assert.Equal("virtio,bridge=vmbr9,link_down=1", line);
    }

    // ── disk ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDisk_ReadsVolumeSizeAndFlags()
    {
        var d = ProxmoxQemuConfigCodec.ParseDisk("scsi0", "local-lvm:vm-200-disk-0,size=32G,ssd=1,discard=on,cache=writeback");

        Assert.Equal("local-lvm:vm-200-disk-0", d.Volume);
        Assert.Equal("32G", d.Size);
        Assert.True(d.Ssd);
        Assert.True(d.Discard);
        Assert.Equal("writeback", d.Cache);
    }

    [Fact]
    public void FormatDisk_PreservesVolumeAndSize_OmitsDisabledFlags()
    {
        var line = ProxmoxQemuConfigCodec.FormatDisk(new(
            "scsi0", Volume: "local-lvm:vm-200-disk-0", Size: "32G", Discard: true, Ssd: false));

        Assert.Contains("local-lvm:vm-200-disk-0", line);
        Assert.Contains("size=32G", line);
        Assert.Contains("discard=on", line);
        Assert.DoesNotContain("ssd", line);   // a disabled flag is simply omitted
    }

    [Fact]
    public void Disk_RoundTrips_PreservingUnknownOptions()
    {
        const string raw = "local-lvm:vm-200-disk-0,size=64G,iothread=1,discard=on";
        var parsed = ProxmoxQemuConfigCodec.ParseDisk("scsi0", raw);

        Assert.Equal("iothread=1", parsed.Extra);   // iothread unmodelled → preserved
        Assert.Equal(Set(raw), Set(ProxmoxQemuConfigCodec.FormatDisk(parsed)));
    }

    private static HashSet<string> Set(string line) =>
        new(line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
