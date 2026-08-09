using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.13.1 — tests for <see cref="ProxmoxLxcCreateValidator"/>: the locally-
/// checkable create guards (vmid range, required template / storage, positive
/// sizes) plus the reused V6.9 network rules. The server stays authoritative for
/// what needs the host (template / storage existence, vmid collisions).
/// </summary>
public class ProxmoxLxcCreateValidatorTests
{
    private static ProxmoxLxcCreate Valid() =>
        new(150, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8, Password: "s3cret");

    [Fact]
    public void ValidSpec_HasNoErrors()
    {
        Assert.Empty(ProxmoxLxcCreateValidator.Validate(Valid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(1_000_000_000)]
    public void VmidOutOfRange_IsRejected(int vmId)
    {
        var errors = ProxmoxLxcCreateValidator.Validate(Valid() with { VmId = vmId });
        Assert.Contains(errors, e => e.Contains("VMID"));
    }

    [Fact]
    public void MissingTemplate_IsRejected()
    {
        var errors = ProxmoxLxcCreateValidator.Validate(Valid() with { OsTemplate = "  " });
        Assert.Contains(errors, e => e.Contains("template", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingRootfsStorage_IsRejected()
    {
        var errors = ProxmoxLxcCreateValidator.Validate(Valid() with { RootfsStorage = "" });
        Assert.Contains(errors, e => e.Contains("storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroRootfsSize_IsRejected()
    {
        var errors = ProxmoxLxcCreateValidator.Validate(Valid() with { RootfsSizeGib = 0 });
        Assert.Contains(errors, e => e.Contains("Root filesystem size"));
    }

    [Fact]
    public void NoPasswordOrSshKey_IsRejected()
    {
        var errors = ProxmoxLxcCreateValidator.Validate(Valid() with { Password = null, SshPublicKeys = null });
        Assert.Contains(errors, e => e.Contains("log in"));
    }

    [Fact]
    public void MalformedNetwork_IsRejected_ViaSharedNetRules()
    {
        var spec = Valid() with { Net0 = new ProxmoxLxcNetChange("net0", Ip: "not-a-cidr", Gw: "999.1.1.1") };
        var errors = ProxmoxLxcCreateValidator.Validate(spec);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidDhcpNetwork_IsAccepted()
    {
        var spec = Valid() with { Net0 = new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Ip: "dhcp") };
        Assert.Empty(ProxmoxLxcCreateValidator.Validate(spec));
    }
}



