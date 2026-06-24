using Stashboard.Core.Abstractions;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V8.0 — tests for <see cref="ProxmoxLxcCloneValidator"/>: the locally-checkable
/// clone guards (destination vmid range, source-snapshot name) and the shared
/// snapshot-name rule. The server stays authoritative for what needs the host
/// (whether a linked clone is possible, vmid collisions, snapshot existence).
/// </summary>
public class ProxmoxLxcCloneValidatorTests
{
    [Fact]
    public void ValidSpec_HasNoErrors()
    {
        Assert.Empty(ProxmoxLxcCloneValidator.Validate(new ProxmoxLxcClone(160, "ct-clone", Full: true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(1_000_000_000)]
    public void NewVmidOutOfRange_IsRejected(int vmId)
    {
        var errors = ProxmoxLxcCloneValidator.Validate(new ProxmoxLxcClone(vmId));
        Assert.Contains(errors, e => e.Contains("VMID"));
    }

    [Fact]
    public void InvalidSourceSnapshotName_IsRejected()
    {
        var errors = ProxmoxLxcCloneValidator.Validate(new ProxmoxLxcClone(160, SnapName: "1bad name"));
        Assert.Contains(errors, e => e.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("before-upgrade", true)]
    [InlineData("snap_1", true)]
    [InlineData("A", true)]
    [InlineData("1leading-digit", false)]
    [InlineData("has space", false)]
    [InlineData("current", false)]   // reserved (the live-state pseudo-snapshot)
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidSnapshotName_MatchesProxmoxRule(string name, bool expected)
    {
        Assert.Equal(expected, ProxmoxLxcCloneValidator.IsValidSnapshotName(name));
    }
}
