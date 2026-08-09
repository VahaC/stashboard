using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.7.1 — unit tests for the deterministic command <see cref="ProxmoxSshUpdateApplier"/>
/// builds (and exposes for the UI preview). The SSH streaming itself needs a
/// live host and isn't unit-tested here, mirroring the inspector.
/// </summary>
public class ProxmoxSshUpdateApplierTests
{
    private static readonly ProxmoxSshUpdateApplier Applier =
        new(NullLogger<ProxmoxSshUpdateApplier>.Instance);

    [Fact]
    public void BuildCommand_Node_RunsAptDirectlyWithoutPctExec()
    {
        var cmd = Applier.BuildCommand(0);

        Assert.StartsWith("sh -c '", cmd);
        Assert.EndsWith("' 2>&1", cmd);
        Assert.DoesNotContain("pct exec", cmd);
        Assert.Contains("apt-get update", cmd);
        Assert.Contains("apt-get -y -o Dpkg::Options::=--force-confold", cmd);
        Assert.Contains("dist-upgrade", cmd);
    }

    [Fact]
    public void BuildCommand_Lxc_WrapsInPctExec()
    {
        var cmd = Applier.BuildCommand(105);

        Assert.StartsWith("pct exec 105 -- sh -c '", cmd);
        Assert.EndsWith("' 2>&1", cmd);
        Assert.Contains("apt-get update && apt-get -y", cmd);
        Assert.Contains("dist-upgrade", cmd);
    }

    [Fact]
    public void BuildCommand_GuardsNonDebianTargets()
    {
        // The non-apt guard must be present (distinct exit code → "nothing to upgrade").
        Assert.Contains("command -v apt-get", Applier.BuildCommand(0));
        Assert.Contains("exit 3", Applier.BuildCommand(110));
    }
}



