using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V8.5 — guards for <see cref="ProxmoxQemuConfigValidator"/>, the locally-checkable
/// safety net run before a VM config write reaches the host. Mirrors the LXC validator
/// tests: a valid spec passes clean, each malformed field surfaces a clean message, and
/// the advanced raw escape hatch is never second-guessed.
/// </summary>
public class ProxmoxQemuConfigValidatorTests
{
    [Fact]
    public void Validate_CleanSpec_NoErrors()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Cores: 4, Sockets: 2, MemoryMib: 4096, BalloonMib: 2048, OsType: "l26",
            Networks: [new ProxmoxQemuNetChange("net0", Model: "virtio", MacAddr: "AA:BB:CC:DD:EE:FF", Tag: 10)]));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9000)]
    public void Validate_CoresOutOfRange_Rejected(int cores)
    {
        Assert.NotEmpty(ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(Cores: cores)));
    }

    [Fact]
    public void Validate_SocketsOutOfRange_Rejected()
    {
        Assert.NotEmpty(ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(Sockets: 8)));
    }

    [Fact]
    public void Validate_BalloonAboveMemory_Rejected()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(MemoryMib: 2048, BalloonMib: 4096));
        Assert.Contains(errors, e => e.Contains("Balloon"));
    }

    [Fact]
    public void Validate_UnknownOsType_Rejected()
    {
        Assert.NotEmpty(ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(OsType: "haiku")));
    }

    [Fact]
    public void Validate_UnknownNicModel_Rejected()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Networks: [new ProxmoxQemuNetChange("net0", Model: "supernic")]));
        Assert.Contains(errors, e => e.Contains("NIC model"));
    }

    [Fact]
    public void Validate_BadMac_Rejected()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Networks: [new ProxmoxQemuNetChange("net0", Model: "virtio", MacAddr: "zz:zz")]));
        Assert.Contains(errors, e => e.Contains("MAC"));
    }

    [Fact]
    public void Validate_BadVlanTag_Rejected()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Networks: [new ProxmoxQemuNetChange("net0", Model: "virtio", Tag: 9999)]));
        Assert.Contains(errors, e => e.Contains("VLAN"));
    }

    [Fact]
    public void Validate_NetRemovalMissingKey_Rejected()
    {
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Networks: [new ProxmoxQemuNetChange("", Remove: true)]));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_RawNic_NotSecondGuessed()
    {
        // The advanced raw path is the user's explicit choice — a nonsense MAC inside
        // Raw is left for Proxmox to reject, not flagged here.
        var errors = ProxmoxQemuConfigValidator.Validate(new ProxmoxQemuConfigUpdate(
            Networks: [new ProxmoxQemuNetChange("net0", Raw: "virtio=nonsense,bridge=vmbr0")]));
        Assert.Empty(errors);
    }

    // ── resize (grow-only) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("+8G")]
    [InlineData("+512M")]
    [InlineData("+1T")]
    public void ValidateResize_PositiveIncrement_Ok(string size)
    {
        Assert.Empty(ProxmoxQemuConfigValidator.ValidateResize("scsi0", size));
    }

    [Theory]
    [InlineData("32G")]    // absolute — could shrink, rejected
    [InlineData("-8G")]    // negative
    [InlineData("0")]
    [InlineData("")]
    public void ValidateResize_NonGrow_Rejected(string size)
    {
        Assert.NotEmpty(ProxmoxQemuConfigValidator.ValidateResize("scsi0", size));
    }

    [Fact]
    public void ValidateResize_MissingDisk_Rejected()
    {
        Assert.NotEmpty(ProxmoxQemuConfigValidator.ValidateResize("", "+8G"));
    }
}
