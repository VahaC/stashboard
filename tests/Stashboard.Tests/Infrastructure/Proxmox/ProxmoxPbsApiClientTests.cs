using System.Net;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// V6.8.3 — tests for the PBS (Proxmox Backup Server) branch of
/// <see cref="ProxmoxApiClient"/>: the <c>PBSAPIToken</c> auth header (the cause
/// of the 401 when a PBS host is added as PVE), the node-status probe in place of
/// LXC discovery, the PBS node-status shape (<c>root</c> fs + <c>info.kversion</c>
/// + <c>/version</c>), and datastores mapped onto the storage-pool shape.
/// </summary>
public class ProxmoxPbsApiClientTests
{
    // ── auth header scheme/separator ──────────────────────────────────────────

    [Fact]
    public void BuildAuthHeader_Pve_UsesEqualsSeparator()
    {
        var h = ProxmoxApiClient.BuildAuthHeader(Profile(ProxmoxServerType.Pve));
        Assert.Equal("PVEAPIToken", h.Scheme);
        Assert.Equal("root@pam!stash=secret", h.Parameter);
    }

    [Fact]
    public void BuildAuthHeader_Pbs_UsesColonSeparator()
    {
        var h = ProxmoxApiClient.BuildAuthHeader(Profile(ProxmoxServerType.Pbs));
        Assert.Equal("PBSAPIToken", h.Scheme);
        Assert.Equal("root@pam!stash:secret", h.Parameter);
    }

    // ── PBS has no LXC: probe node status, return no guests ───────────────────

    [Fact]
    public async Task ListLxc_Pbs_ProbesNodeStatus_AndReturnsNoGuests()
    {
        string? path = null;
        string? auth = null;
        var client = BuildClient(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            auth = req.Headers.Authorization?.ToString();
            return Json(PbsStatusJson);
        });

        var guests = await client.ListLxcAsync(Profile(ProxmoxServerType.Pbs));

        Assert.Empty(guests);
        Assert.Equal("/api2/json/nodes/pbs-main/status", path);   // probed status, not /lxc
        Assert.Equal("PBSAPIToken root@pam!stash:secret", auth);
    }

    // ── PBS node status shape ─────────────────────────────────────────────────

    [Fact]
    public async Task GetNodeStatus_Pbs_ParsesRootFs_KernelFromInfo_AndVersion()
    {
        var client = BuildClient(req =>
        {
            var p = req.RequestUri!.AbsolutePath;
            if (p.EndsWith("/version")) return Json("""{"data":{"version":"3.2.7","release":"1"}}""");
            if (p.EndsWith("/status")) return Json(PbsStatusJson);
            return Json("""{"data":{}}""");   // subscription etc.
        });

        var s = await client.GetNodeStatusAsync(Profile(ProxmoxServerType.Pbs));

        Assert.Equal(100_000_000_000L, s.RootTotal);   // from "root", not "rootfs"
        Assert.Equal(25_000_000_000L, s.RootUsed);
        Assert.Equal("Linux 6.8.4-pve", s.KernelVersion);   // from info.kversion
        Assert.Equal("PBS 3.2.7", s.PveVersion);            // from /version
        Assert.Equal(16_000_000_000L, s.MemTotal);
    }

    // ── PBS datastores → storage-pool shape ───────────────────────────────────

    [Fact]
    public async Task GetNodeStorages_Pbs_MapsDatastoreUsage()
    {
        const string datastores = """
            {"data":[
              {"store":"backup-hdd","total":4000000000000,"used":1500000000000,"avail":2500000000000},
              {"store":"backup-ssd","total":1000000000000,"used":900000000000}
            ]}
            """;
        string? path = null;
        var client = BuildClient(req => { path = req.RequestUri!.AbsolutePath; return Json(datastores); });

        var pools = await client.GetNodeStoragesAsync(Profile(ProxmoxServerType.Pbs));

        Assert.Equal("/api2/json/status/datastore-usage", path);   // not /nodes/{node}/storage
        Assert.Equal(2, pools.Count);
        var hdd = pools.Single(p => p.Storage == "backup-hdd");
        Assert.Equal("datastore", hdd.Type);
        Assert.True(hdd.Active);
        Assert.Equal(4000000000000L, hdd.Total);
        Assert.Equal(1500000000000L, hdd.Used);
        Assert.Equal(2500000000000L, hdd.Avail);
        // avail missing on the second → derived as total - used.
        var ssd = pools.Single(p => p.Storage == "backup-ssd");
        Assert.Equal(100000000000L, ssd.Avail);
    }

    // ── V7.2.1 — PBS disks/list uses disk-type / status, not type / health ────

    [Fact]
    public async Task GetNodeDisks_Pbs_ReadsDiskTypeAndStatus_ForHealthBadge()
    {
        // PBS names the SMART-health column "status" (e.g. "passed") and the disk
        // type column "disk-type" (e.g. "hdd"), where PVE uses "health" / "type".
        // Reading only the PVE keys left the type blank and the health "UNKNOWN".
        const string disks = """
            {"data":[
              {"devpath":"/dev/sda","model":"ST1000DM010","size":1000204886016,"disk-type":"hdd","status":"passed"},
              {"devpath":"/dev/sdb","model":"LITEONIT","size":128035676160,"disk-type":"ssd","status":"passed","wearout":100}
            ]}
            """;
        var client = BuildClient(_ => Json(disks));

        var result = await client.GetNodeDisksAsync(Profile(ProxmoxServerType.Pbs));

        var sda = result.Single(d => d.DevPath == "/dev/sda");
        Assert.Equal("hdd", sda.Type);          // from "disk-type"
        Assert.Equal("passed", sda.Health);     // from "status", not the "unknown" fallback
        var sdb = result.Single(d => d.DevPath == "/dev/sdb");
        Assert.Equal("ssd", sdb.Type);
        Assert.Equal("passed", sdb.Health);
        Assert.Equal(100, sdb.WearoutPercent);
    }

    // ── V7.2.1 — PBS SMART disk param is the bare name, not /dev/sda ──────────

    [Fact]
    public async Task GetNodeDiskSmart_Pbs_StripsDevPrefix_FromDiskParam()
    {
        // PBS validates `disk` against its block-device name schema (bare name),
        // and rejects the /dev/-prefixed path PVE accepts with a 400. We must send
        // "sda", not "/dev/sda".
        string? query = null;
        var client = BuildClient(req =>
        {
            query = req.RequestUri!.Query;
            return Json("""{"data":{"health":"PASSED","type":"text","text":"ok"}}""");
        });

        var smart = await client.GetNodeDiskSmartAsync(Profile(ProxmoxServerType.Pbs), "/dev/sda");

        Assert.Contains("disk=sda", query);
        Assert.DoesNotContain("%2Fdev%2F", query);   // no /dev/ prefix reaches PBS
        Assert.Equal("ok", smart.Text);              // and the response still parses
    }

    // ── PVE still uses the PVE storage endpoint ───────────────────────────────

    [Fact]
    public async Task GetNodeStorages_Pve_UsesNodeStorageEndpoint()
    {
        string? path = null;
        var client = BuildClient(req => { path = req.RequestUri!.AbsolutePath; return Json("""{"data":[]}"""); });

        await client.GetNodeStoragesAsync(Profile(ProxmoxServerType.Pve));

        Assert.Equal("/api2/json/nodes/pbs-main/storage", path);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private const string PbsStatusJson = """
        {"data":{"cpu":0.05,"wait":0.01,"uptime":99999,"loadavg":["0.1","0.2","0.3"],
        "memory":{"total":16000000000,"used":4000000000,"free":12000000000},
        "swap":{"total":0,"used":0},
        "root":{"total":100000000000,"used":25000000000,"avail":75000000000},
        "info":{"kversion":"Linux 6.8.4-pve"},
        "cpuinfo":{"model":"Intel","cpus":8,"sockets":1,"cores":4,"mhz":"2400","hvm":1}}}
        """;

    private static ProxmoxConnectionProfile Profile(ProxmoxServerType serverType) =>
        new("https://pbs.lan:8007", "pbs-main", "root@pam!stash", "secret",
            SkipTlsVerify: false, Ssh: null, ServerType: serverType);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private static ProxmoxApiClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken _) => Task.FromResult(responder(req)));

        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ProxmoxApiClient.HttpClientName)).Returns(httpClient);

        return new ProxmoxApiClient(factory.Object);
    }
}
