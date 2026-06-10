using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// Unit tests for the <see cref="ProxmoxUpdateChecker"/> orchestrator. The API
/// client and SSH guest inspector are mocked — those carry their own transport
/// concerns — so these tests only exercise the composition / error-routing
/// logic the orchestrator owns. Mirrors <c>DockerUpdateCheckerTests</c>.
/// </summary>
public class ProxmoxUpdateCheckerTests
{
    // ── CheckAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_ReturnsNodeRowPlusOneRowPerLxc()
    {
        var api = MockApi(
            lxcs: [new ProxmoxLxcSummary(105, "pihole", true), new ProxmoxLxcSummary(110, "db", true)],
            nodeUpdates: 4);
        var inspector = MockInspector((105, new ProxmoxGuestInspection(7, null)), (110, new ProxmoxGuestInspection(0, null)));
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        Assert.Null(result.Error);
        Assert.Equal(3, result.Guests.Count);

        var node = result.Guests.Single(g => g.GuestType == ProxmoxGuestType.Node);
        Assert.Equal(0, node.VmId);
        Assert.Equal("pve", node.Name);
        Assert.Equal(4, node.PendingUpdates);

        var pihole = result.Guests.Single(g => g.VmId == 105);
        Assert.Equal(ProxmoxGuestType.Lxc, pihole.GuestType);
        Assert.Equal(7, pihole.PendingUpdates);
        Assert.Equal(0, result.Guests.Single(g => g.VmId == 110).PendingUpdates);
    }

    [Fact]
    public async Task CheckAsync_ApiUnreachable_ReturnsConnectionLevelErrorAndNoGuests()
    {
        var api = new Mock<IProxmoxApiClient>();
        api.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var inspector = new Mock<IProxmoxGuestInspector>();
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        Assert.NotNull(result.Error);
        Assert.Contains("connection refused", result.Error);
        Assert.Empty(result.Guests);
        // The inspector must never be touched once the API probe failed.
        inspector.Verify(i => i.CountPendingUpdatesAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_StoppedLxc_IsNotInspectedAndHasNullCount()
    {
        var api = MockApi(lxcs: [new ProxmoxLxcSummary(105, "pihole", false)], nodeUpdates: 0);
        var inspector = new Mock<IProxmoxGuestInspector>();
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        var lxc = result.Guests.Single(g => g.VmId == 105);
        Assert.False(lxc.IsRunning);
        Assert.Null(lxc.PendingUpdates);
        inspector.Verify(i => i.CountPendingUpdatesAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_NoSshConfigured_RunningLxcReportsError()
    {
        var api = MockApi(lxcs: [new ProxmoxLxcSummary(105, "pihole", true)], nodeUpdates: 2);
        var inspector = new Mock<IProxmoxGuestInspector>();
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileNoSsh());

        var lxc = result.Guests.Single(g => g.VmId == 105);
        Assert.Null(lxc.PendingUpdates);
        Assert.Contains("SSH", lxc.Error);
        // Node row still resolves over the API.
        Assert.Equal(2, result.Guests.Single(g => g.GuestType == ProxmoxGuestType.Node).PendingUpdates);
    }

    [Fact]
    public async Task CheckAsync_NodeAptFails_NodeRowCarriesErrorButLxcsStillChecked()
    {
        var api = new Mock<IProxmoxApiClient>();
        api.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProxmoxLxcSummary(105, "pihole", true)]);
        api.Setup(a => a.ListQemuAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        api.Setup(a => a.GetNodePendingUpdatesAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("apt endpoint 500"));
        var inspector = MockInspector((105, new ProxmoxGuestInspection(3, null)));
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        Assert.Null(result.Error); // connection-level OK — listing succeeded
        var node = result.Guests.Single(g => g.GuestType == ProxmoxGuestType.Node);
        Assert.Null(node.PendingUpdates);
        Assert.Contains("apt endpoint 500", node.Error);
        Assert.Equal(3, result.Guests.Single(g => g.VmId == 105).PendingUpdates);
    }

    [Fact]
    public async Task CheckAsync_EnrichesRunningLxcWithIpAndResourceFields()
    {
        var summary = new ProxmoxLxcSummary(
            105, "pihole", true,
            UptimeSeconds: 3600, CpuCores: 2, MemoryBytes: 536870912, DiskBytes: 8589934592, Tags: "dns;prod");
        var api = MockApi(lxcs: [summary], nodeUpdates: 0);
        api.Setup(a => a.GetLxcIpAddressAsync(It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync("192.168.1.50");
        var inspector = MockInspector((105, new ProxmoxGuestInspection(3, null)));
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        var lxc = result.Guests.Single(g => g.VmId == 105);
        Assert.Equal("192.168.1.50", lxc.IpAddress);
        Assert.Equal(3600, lxc.UptimeSeconds);
        Assert.Equal(2, lxc.CpuCores);
        Assert.Equal(536870912, lxc.MemoryBytes);
        Assert.Equal(8589934592, lxc.DiskBytes);
        Assert.Equal("dns;prod", lxc.Tags);
        Assert.Equal(3, lxc.PendingUpdates);
    }

    [Fact]
    public async Task CheckAsync_StoppedLxc_HasNoIpButKeepsResourceFields()
    {
        var summary = new ProxmoxLxcSummary(105, "pihole", false, CpuCores: 1, MemoryBytes: 268435456);
        var api = MockApi(lxcs: [summary], nodeUpdates: 0);
        var checker = Build(api, new Mock<IProxmoxGuestInspector>());

        var result = await checker.CheckAsync(ProfileWithSsh());

        var lxc = result.Guests.Single(g => g.VmId == 105);
        Assert.Null(lxc.IpAddress);
        Assert.Equal(1, lxc.CpuCores);
        Assert.Equal(268435456, lxc.MemoryBytes);
        // A stopped guest is never probed for its IP.
        api.Verify(a => a.GetLxcIpAddressAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_DisabledLxc_IsListedButNotInspected_NullCount()
    {
        // pihole (105) has monitoring off; db (110) is on. Both are running.
        var summary105 = new ProxmoxLxcSummary(105, "pihole", true, CpuCores: 2, MemoryBytes: 536870912);
        var api = MockApi(lxcs: [summary105, new ProxmoxLxcSummary(110, "db", true)], nodeUpdates: 4);
        var inspector = MockInspector(
            (105, new ProxmoxGuestInspection(7, null)),
            (110, new ProxmoxGuestInspection(3, null)));
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh(), new HashSet<int> { 105 });

        // The disabled LXC is still discovered (resource fields survive) but its
        // count isn't computed and the SSH inspector is never touched for it.
        var pihole = result.Guests.Single(g => g.VmId == 105);
        Assert.True(pihole.IsRunning);
        Assert.Null(pihole.PendingUpdates);
        Assert.Equal(2, pihole.CpuCores);
        Assert.Equal(536870912, pihole.MemoryBytes);
        inspector.Verify(i => i.CountPendingUpdatesAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(a => a.GetLxcIpAddressAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()), Times.Never);

        // The enabled LXC and the node are unaffected.
        Assert.Equal(3, result.Guests.Single(g => g.VmId == 110).PendingUpdates);
        Assert.Equal(4, result.Guests.Single(g => g.GuestType == ProxmoxGuestType.Node).PendingUpdates);
    }

    // ── V6.14 — QEMU VMs ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_MapsQemuVms_WithQemuTypeAndNoUpdateCount()
    {
        // One LXC + two VMs + the node. VMs carry resource detail but never a
        // pending-update count, an IP, or an SSH probe — update monitoring is
        // LXC-only.
        var api = MockApi(
            lxcs: [new ProxmoxLxcSummary(105, "pihole", true)],
            nodeUpdates: 2,
            vms: [
                new ProxmoxLxcSummary(200, "win11", true, UptimeSeconds: 7200, CpuCores: 4, MemoryBytes: 8589934592, DiskBytes: 137438953472, Tags: "vm;prod"),
                new ProxmoxLxcSummary(201, "ubuntu", false, CpuCores: 2),
            ]);
        var inspector = MockInspector((105, new ProxmoxGuestInspection(1, null)));
        var checker = Build(api, inspector);

        var result = await checker.CheckAsync(ProfileWithSsh());

        // node + 1 LXC + 2 VMs
        Assert.Equal(4, result.Guests.Count);

        var win = result.Guests.Single(g => g.VmId == 200);
        Assert.Equal(ProxmoxGuestType.Qemu, win.GuestType);
        Assert.True(win.IsRunning);
        Assert.Null(win.PendingUpdates);     // VMs aren't apt-monitored
        Assert.Null(win.IpAddress);          // no guest-agent IP lookup
        Assert.Equal(7200, win.UptimeSeconds);
        Assert.Equal(4, win.CpuCores);
        Assert.Equal(8589934592, win.MemoryBytes);
        Assert.Equal("vm;prod", win.Tags);

        var ubuntu = result.Guests.Single(g => g.VmId == 201);
        Assert.Equal(ProxmoxGuestType.Qemu, ubuntu.GuestType);
        Assert.False(ubuntu.IsRunning);
        Assert.Null(ubuntu.PendingUpdates);

        // A running VM is never inspected over SSH (no `pct exec` for QEMU).
        inspector.Verify(i => i.CountPendingUpdatesAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 200, It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(a => a.GetLxcIpAddressAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 200, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_QemuListingFails_ReturnsConnectionLevelError()
    {
        // The VM listing rides on the same reachable host as the LXC probe; a
        // failure there is a connection-level error (keep the last-known cards),
        // exactly like the LXC listing failing.
        var api = MockApi(lxcs: [new ProxmoxLxcSummary(105, "pihole", true)], nodeUpdates: 0);
        api.Setup(a => a.ListQemuAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("qemu 500"));
        var checker = Build(api, new Mock<IProxmoxGuestInspector>());

        var result = await checker.CheckAsync(ProfileWithSsh());

        Assert.NotNull(result.Error);
        Assert.Contains("qemu 500", result.Error);
        Assert.Empty(result.Guests);
    }

    // ── TestConnectionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_ApiAndSshBothOk_ReportsBothReachable()
    {
        var api = MockApi(lxcs: [], nodeUpdates: 0);
        var inspector = new Mock<IProxmoxGuestInspector>();
        inspector.Setup(i => i.TestConnectionAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var checker = Build(api, inspector);

        var result = await checker.TestConnectionAsync(ProfileWithSsh());

        Assert.True(result.ApiReachable);
        Assert.True(result.SshReachable);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestConnection_NoSsh_SshReachableIsNull()
    {
        var api = MockApi(lxcs: [], nodeUpdates: 0);
        var inspector = new Mock<IProxmoxGuestInspector>();
        var checker = Build(api, inspector);

        var result = await checker.TestConnectionAsync(ProfileNoSsh());

        Assert.True(result.ApiReachable);
        Assert.Null(result.SshReachable);
        inspector.Verify(i => i.TestConnectionAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestConnection_ApiFails_ReportsErrorAndNotReachable()
    {
        var api = new Mock<IProxmoxApiClient>();
        api.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("401 Unauthorized"));
        var inspector = new Mock<IProxmoxGuestInspector>();
        var checker = Build(api, inspector);

        var result = await checker.TestConnectionAsync(ProfileNoSsh());

        Assert.False(result.ApiReachable);
        Assert.Contains("401", result.Error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Mock<IProxmoxApiClient> MockApi(
        IReadOnlyList<ProxmoxLxcSummary> lxcs, int nodeUpdates, IReadOnlyList<ProxmoxLxcSummary>? vms = null)
    {
        var api = new Mock<IProxmoxApiClient>();
        api.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lxcs);
        // V6.14 — the checker now also lists QEMU VMs; default to none so the
        // existing LXC-focused assertions are unaffected.
        api.Setup(a => a.ListQemuAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(vms ?? []);
        api.Setup(a => a.GetNodePendingUpdatesAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodeUpdates);
        return api;
    }

    private static Mock<IProxmoxGuestInspector> MockInspector(params (int VmId, ProxmoxGuestInspection Result)[] inspections)
    {
        var inspector = new Mock<IProxmoxGuestInspector>();
        foreach (var (vmId, result) in inspections)
            inspector.Setup(i => i.CountPendingUpdatesAsync(
                    It.IsAny<ProxmoxConnectionProfile>(), vmId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        return inspector;
    }

    private static ProxmoxUpdateChecker Build(Mock<IProxmoxApiClient> api, Mock<IProxmoxGuestInspector> inspector) =>
        new(api.Object, inspector.Object, NullLogger<ProxmoxUpdateChecker>.Instance);

    private static ProxmoxConnectionProfile ProfileWithSsh() =>
        new("https://pve.lan:8006", "pve", "root@pam!stash", "secret", true,
            new DockerSshCredentials("pve.lan", 22, "root", "KEY", null, string.Empty));

    private static ProxmoxConnectionProfile ProfileNoSsh() =>
        new("https://pve.lan:8006", "pve", "root@pam!stash", "secret", true, null);
}
