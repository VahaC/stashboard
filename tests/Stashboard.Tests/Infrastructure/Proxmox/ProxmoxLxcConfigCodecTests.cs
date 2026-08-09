using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.9 — round-trip + formatting tests for <see cref="ProxmoxLxcConfigCodec"/>,
/// the layer that turns the compound <c>net&lt;n&gt;</c> / <c>mp&lt;n&gt;</c> /
/// <c>rootfs</c> option lines into the structured edit models and back. The
/// round-trip assertions compare option <em>sets</em> (Proxmox doesn't care about
/// option order, and the formatter emits a stable canonical order), so the key
/// guarantee under test is "no option is dropped or invented".
/// </summary>
public class ProxmoxLxcConfigCodecTests
{
    // ── net<n> ────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseNet_ReadsStructuredFields()
    {
        var n = ProxmoxLxcConfigCodec.ParseNet(
            "net0", "name=eth0,bridge=vmbr0,ip=192.168.1.5/24,gw=192.168.1.1,hwaddr=AA:BB:CC:DD:EE:FF,tag=10,firewall=1,rate=10,mtu=1400,link_down=0");

        Assert.Equal("net0", n.Key);
        Assert.Equal("eth0", n.Name);
        Assert.Equal("vmbr0", n.Bridge);
        Assert.Equal("192.168.1.5/24", n.Ip);
        Assert.Equal("192.168.1.1", n.Gw);
        Assert.Equal("AA:BB:CC:DD:EE:FF", n.Hwaddr);
        Assert.Equal(10, n.Tag);
        Assert.True(n.Firewall);
        Assert.Equal(10, n.Rate);
        Assert.Equal(1400, n.Mtu);
        Assert.False(n.LinkDown);
        Assert.Null(n.Extra);
    }

    [Fact]
    public void ParseNet_PreservesUnknownOptionsAsExtra()
    {
        var n = ProxmoxLxcConfigCodec.ParseNet("net0", "name=eth0,bridge=vmbr0,trunks=2-4,mtu=1500");

        Assert.Equal("eth0", n.Name);
        Assert.Equal(1500, n.Mtu);
        Assert.Equal("trunks=2-4", n.Extra);   // unmodelled option kept verbatim
    }

    [Fact]
    public void FormatNet_RoundTripsWithoutLosingOptions()
    {
        const string raw = "name=eth0,bridge=vmbr0,ip=dhcp,firewall=1,trunks=2-4";
        var formatted = ProxmoxLxcConfigCodec.FormatNet(ProxmoxLxcConfigCodec.ParseNet("net0", raw));
        Assert.Equal(OptionSet(raw), OptionSet(formatted));
    }

    [Fact]
    public void FormatNet_RawWins_Verbatim()
    {
        var formatted = ProxmoxLxcConfigCodec.FormatNet(
            new ProxmoxLxcNetChange("net0", Raw: "name=eth0,bridge=vmbr9,something=weird", Name: "ignored"));
        Assert.Equal("name=eth0,bridge=vmbr9,something=weird", formatted);
    }

    [Fact]
    public void FormatNet_OmitsNullFields_EmitsFalseBoolsExplicitly()
    {
        var formatted = ProxmoxLxcConfigCodec.FormatNet(
            new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Firewall: false));
        var set = OptionSet(formatted);
        Assert.Contains("name=eth0", set);
        Assert.Contains("bridge=vmbr0", set);
        Assert.Contains("firewall=0", set);     // false ⇒ explicit, not dropped
        Assert.DoesNotContain(set, s => s.StartsWith("tag="));
        Assert.DoesNotContain(set, s => s.StartsWith("mtu="));
    }

    // ── mp<n> ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseMount_ReadsPositionalVolumeAndOptions()
    {
        var m = ProxmoxLxcConfigCodec.ParseMount(
            "mp0", "local-lvm:vm-101-disk-1,mp=/data,size=20G,backup=1,ro=0,acl=1,mountoptions=noatime");

        Assert.Equal("mp0", m.Key);
        Assert.Equal("local-lvm:vm-101-disk-1", m.Volume);
        Assert.Equal("/data", m.MountPoint);
        Assert.Equal("20G", m.Size);
        Assert.True(m.Backup);
        Assert.False(m.ReadOnly);
        Assert.True(m.Acl);
        Assert.Equal("noatime", m.MountOptions);
    }

    [Fact]
    public void FormatMount_RoundTripsBindMount()
    {
        const string raw = "/host/path,mp=/inside,backup=0,ro=1";
        var formatted = ProxmoxLxcConfigCodec.FormatMount(ProxmoxLxcConfigCodec.ParseMount("mp1", raw));
        Assert.Equal(OptionSet(raw), OptionSet(formatted));
        Assert.StartsWith("/host/path,", formatted);   // positional volume stays first
    }

    [Fact]
    public void FormatMount_RoundTripsStorageVolumeWithUnknownOption()
    {
        const string raw = "local-lvm:vm-101-disk-1,mp=/data,size=20G,future-opt=1";
        var formatted = ProxmoxLxcConfigCodec.FormatMount(ProxmoxLxcConfigCodec.ParseMount("mp0", raw));
        Assert.Equal(OptionSet(raw), OptionSet(formatted));
    }

    // ── rootfs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseRootfs_ReadsVolumeAndSize()
    {
        var r = ProxmoxLxcConfigCodec.ParseRootfs("local-lvm:vm-101-disk-0,size=8G,ro=0");
        Assert.Equal("local-lvm:vm-101-disk-0", r.Volume);
        Assert.Equal("8G", r.Size);
        Assert.False(r.ReadOnly);
    }

    [Fact]
    public void FormatRootfs_RoundTrips()
    {
        const string raw = "local-lvm:vm-101-disk-0,size=8G,acl=1";
        var formatted = ProxmoxLxcConfigCodec.FormatRootfs(ProxmoxLxcConfigCodec.ParseRootfs(raw));
        Assert.Equal(OptionSet(raw), OptionSet(formatted));
    }

    /// <summary>An order-independent view of a raw option line.</summary>
    private static HashSet<string> OptionSet(string raw) =>
        new(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}


