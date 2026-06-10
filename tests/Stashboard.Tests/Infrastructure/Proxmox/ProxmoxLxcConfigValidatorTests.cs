using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.9 — the locally-checkable safety guards for a structured LXC config write.
/// These run before anything reaches the host, so the user gets a clean message
/// for the mistakes Stashboard can verify offline (bad IP/CIDR, duplicate
/// names/paths, removing rootfs, impossible sizes).
/// </summary>
public class ProxmoxLxcConfigValidatorTests
{
    [Fact]
    public void Valid_StaticInterface_PassesClean()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0",
                Ip: "192.168.1.5/24", Gw: "192.168.1.1", Hwaddr: "AA:BB:CC:DD:EE:FF", Tag: 10, Mtu: 1400)]));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("dhcp")]
    [InlineData("manual")]
    [InlineData("10.0.0.2/8")]
    public void Net_AcceptsValidIpModes(string ip)
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Ip: ip)]));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("192.168.1.5")]      // missing prefix
    [InlineData("999.1.1.1/24")]     // bad octet
    [InlineData("notanip")]
    public void Net_RejectsBadStaticIp(string ip)
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Ip: ip)]));
        Assert.Contains(errors, e => e.Contains("IPv4 CIDR"));
    }

    [Fact]
    public void Net_RejectsBadGateway()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Gw: "192.168.1.999")]));
        Assert.Contains(errors, e => e.Contains("gateway"));
    }

    [Fact]
    public void Net_RejectsBadMac_AndOutOfRangeTagMtu()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Hwaddr: "zz:zz", Tag: 9000, Mtu: 10)]));
        Assert.Contains(errors, e => e.Contains("MAC"));
        Assert.Contains(errors, e => e.Contains("VLAN tag"));
        Assert.Contains(errors, e => e.Contains("MTU"));
    }

    [Fact]
    public void Net_RejectsDuplicateInterfaceNames()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks:
            [
                new ProxmoxLxcNetChange("net0", Name: "eth0", Ip: "dhcp"),
                new ProxmoxLxcNetChange("", Name: "eth0", Ip: "dhcp"),
            ]));
        Assert.Contains(errors, e => e.Contains("Duplicate interface name"));
    }

    [Fact]
    public void Net_RawMode_SkipsStructuredValidation()
    {
        // Advanced/raw is the user's explicit choice — the host validates it.
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Raw: "this is not validated, gw=bogus")]));
        Assert.Empty(errors);
    }

    [Fact]
    public void Mount_RejectsRootfsRemoval()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Mounts: [new ProxmoxLxcMountChange("rootfs", Remove: true)]));
        Assert.Contains(errors, e => e.Contains("rootfs cannot be removed"));
    }

    [Fact]
    public void Mount_RejectsRelativeMountPath_AndDuplicatePaths()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Mounts:
            [
                new ProxmoxLxcMountChange("mp0", Volume: "local-lvm:8", MountPoint: "data"),
                new ProxmoxLxcMountChange("mp1", Volume: "local-lvm:8", MountPoint: "/shared"),
                new ProxmoxLxcMountChange("", Volume: "local-lvm:8", MountPoint: "/shared"),
            ]));
        Assert.Contains(errors, e => e.Contains("must be absolute"));
        Assert.Contains(errors, e => e.Contains("Duplicate mount path"));
    }

    [Fact]
    public void Mount_RejectsImpossibleSize()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(
            Mounts: [new ProxmoxLxcMountChange("mp0", Volume: "local-lvm:8", MountPoint: "/data", Size: "huge")]));
        Assert.Contains(errors, e => e.Contains("valid size"));
    }

    [Fact]
    public void Rootfs_RejectsBadSize_AcceptsGoodSize()
    {
        Assert.Contains(
            ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(Rootfs: new ProxmoxLxcRootfsChange(Size: "8GB!"))),
            e => e.Contains("valid size"));
        Assert.Empty(
            ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(Rootfs: new ProxmoxLxcRootfsChange(Size: "16G"))));
    }

    [Fact]
    public void ScalarOnlyUpdate_HasNoStructuredErrors()
    {
        var errors = ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(Cores: 4, MemoryMib: 2048));
        Assert.Empty(errors);   // V6.5 scalar edits are unaffected
    }
}
