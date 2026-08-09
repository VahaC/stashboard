using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V8.4 — tests for <see cref="ProxmoxQemuCreateValidator"/>: the locally-checkable
/// VM-create guards (vmid range, required disk storage / bridge, positive sizes,
/// known bios / ostype / machine, valid MAC / VLAN, ISO sanity). The server stays
/// authoritative for what needs the host (storage / ISO existence, vmid collisions).
/// </summary>
public class ProxmoxQemuCreateValidatorTests
{
    private static ProxmoxQemuCreate Valid() =>
        new(150, "local-lvm", 32, Name: "vm", Bridge: "vmbr0", IsoVolid: "local:iso/debian-12.iso");

    [Fact]
    public void ValidSpec_HasNoErrors()
    {
        Assert.Empty(ProxmoxQemuCreateValidator.Validate(Valid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(1_000_000_000)]
    public void VmidOutOfRange_IsRejected(int vmId)
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { VmId = vmId });
        Assert.Contains(errors, e => e.Contains("VMID"));
    }

    [Fact]
    public void MissingDiskStorage_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { DiskStorage = "  " });
        Assert.Contains(errors, e => e.Contains("disk storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroDiskSize_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { DiskSizeGib = 0 });
        Assert.Contains(errors, e => e.Contains("Disk size"));
    }

    [Fact]
    public void MissingBridge_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { Bridge = "" });
        Assert.Contains(errors, e => e.Contains("bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownOsType_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { OsType = "plan9" });
        Assert.Contains(errors, e => e.Contains("OS type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownBios_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { Bios = "coreboot" });
        Assert.Contains(errors, e => e.Contains("BIOS"));
    }

    [Fact]
    public void OvmfBios_IsAccepted()
    {
        Assert.Empty(ProxmoxQemuCreateValidator.Validate(Valid() with { Bios = "ovmf" }));
    }

    [Fact]
    public void SocketsOutOfRange_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { Sockets = 5 });
        Assert.Contains(errors, e => e.Contains("Sockets"));
    }

    [Fact]
    public void MalformedMac_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { MacAddr = "zz:zz" });
        Assert.Contains(errors, e => e.Contains("MAC"));
    }

    [Fact]
    public void NonIsoMedia_IsRejected()
    {
        var errors = ProxmoxQemuCreateValidator.Validate(Valid() with { IsoVolid = "local:vztmpl/debian-12.tar.zst" });
        Assert.Contains(errors, e => e.Contains("ISO"));
    }

    [Fact]
    public void NoMedia_IsAccepted()
    {
        // A VM with no install media is valid (boot from disk / PXE later).
        Assert.Empty(ProxmoxQemuCreateValidator.Validate(Valid() with { IsoVolid = null }));
    }
}



