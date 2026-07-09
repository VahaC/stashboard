using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V8.1 — tests for <see cref="ProxmoxLxcRestoreValidator"/>: the locally-checkable
/// restore guards (vmid range, the volid must be a vzdump-lxc archive, positive
/// resource overrides). Unlike create there is no password / template / size to
/// validate — the archive carries the rootfs sizes and the container's own config.
/// </summary>
public class ProxmoxLxcRestoreValidatorTests
{
    private static ProxmoxLxcCreate Valid() =>
        new(150, "local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst", "local-lvm", 0, Restore: true);

    [Fact]
    public void ValidSpec_HasNoErrors()
    {
        Assert.Empty(ProxmoxLxcRestoreValidator.Validate(Valid()));
    }

    [Fact]
    public void NoStorageOverride_IsAllowed()
    {
        // Blank storage ⇒ restore each volume onto the storage it was backed up from.
        Assert.Empty(ProxmoxLxcRestoreValidator.Validate(Valid() with { RootfsStorage = "" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(1_000_000_000)]
    public void VmidOutOfRange_IsRejected(int vmId)
    {
        var errors = ProxmoxLxcRestoreValidator.Validate(Valid() with { VmId = vmId });
        Assert.Contains(errors, e => e.Contains("VMID"));
    }

    [Fact]
    public void EmptyBackupVolid_IsRejected()
    {
        var errors = ProxmoxLxcRestoreValidator.Validate(Valid() with { OsTemplate = "" });
        Assert.Contains(errors, e => e.Contains("backup archive"));
    }

    [Fact]
    public void NonBackupVolume_IsRejected()
    {
        // An ISO / template / disk volume is not a restorable LXC archive.
        var errors = ProxmoxLxcRestoreValidator.Validate(Valid() with { OsTemplate = "local:iso/debian.iso" });
        Assert.Contains(errors, e => e.Contains("LXC backup archive"));
    }

    [Fact]
    public void QemuArchive_IsRejected()
    {
        var errors = ProxmoxLxcRestoreValidator.Validate(
            Valid() with { OsTemplate = "local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst" });
        Assert.Contains(errors, e => e.Contains("LXC backup archive"));
    }

    [Fact]
    public void NegativeSwapOverride_IsRejected()
    {
        var errors = ProxmoxLxcRestoreValidator.Validate(Valid() with { SwapMib = -1 });
        Assert.Contains(errors, e => e.Contains("Swap"));
    }

    // ── V8.3 — VM (qemu) restore: the kind flag swaps the expected archive marker ──

    [Fact]
    public void Qemu_ValidQemuArchive_HasNoErrors()
    {
        var spec = Valid() with { OsTemplate = "local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst" };
        Assert.Empty(ProxmoxLxcRestoreValidator.Validate(spec, qemu: true));
    }

    [Fact]
    public void Qemu_LxcArchive_IsRejected()
    {
        // A vzdump-lxc archive is not a restorable VM archive.
        var errors = ProxmoxLxcRestoreValidator.Validate(Valid(), qemu: true);
        Assert.Contains(errors, e => e.Contains("VM backup archive"));
    }
}
