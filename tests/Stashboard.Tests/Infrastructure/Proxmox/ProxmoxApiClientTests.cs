using System.Net;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Infrastructure.Proxmox;

/// <summary>
/// Unit tests for <see cref="ProxmoxApiClient"/> — the JSON-parsing HTTP layer
/// behind the V6.2 Config tab and V6.3 Stats / Tasks tabs. The HTTP transport
/// is mocked with the same pattern as <c>GitHubReleaseClientTests</c>, so these
/// exercise the request-URL construction and the defensive Proxmox JSON parsing
/// (which is loose about number-vs-string typing and MiB-vs-byte units).
/// </summary>
public class ProxmoxApiClientTests
{
    private const string ConfigJson = """
        {"data":{"cores":2,"memory":2048,"swap":512,"hostname":"wireguard",
        "ostype":"debian","arch":"amd64","onboot":1,"unprivileged":1,
        "features":"nesting=1","rootfs":"local-lvm:vm-101-disk-0,size=8G",
        "mp0":"local-lvm:vm-101-disk-1,mp=/data,size=20G",
        "net0":"name=eth0,bridge=vmbr0,ip=dhcp","net1":"name=eth1,bridge=vmbr1,ip=dhcp",
        "digest":"abc"}}
        """;

    private const string StatusJson = """
        {"data":{"status":"running","cpu":0.0123,"mem":123456789,"maxmem":2147483648,
        "disk":1000,"maxdisk":8589934592,"uptime":3600}}
        """;

    // ── V6.2 — GetLxcDetailAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetLxcDetail_ParsesConfigAndStatus()
    {
        var client = BuildClient(ByPath());

        var d = await client.GetLxcDetailAsync(Profile(), 101);

        // config
        Assert.Equal(2, d.Cores);
        Assert.Equal(2048L * 1024 * 1024, d.MemoryBytes);   // MiB → bytes
        Assert.Equal(512L * 1024 * 1024, d.SwapBytes);
        Assert.Equal("wireguard", d.Hostname);
        Assert.Equal("debian", d.OsType);
        Assert.Equal("amd64", d.Arch);
        Assert.True(d.Onboot);
        Assert.True(d.Unprivileged);
        Assert.Equal("nesting=1", d.Features);
        Assert.Equal(["net0", "net1"], d.Networks.Select(n => n.Key));
        Assert.Equal(["mp0", "rootfs"], d.Mounts.Select(m => m.Key));   // ordinal sort: 'm' < 'r'
        Assert.Contains("/data", d.Mounts.Single(m => m.Key == "mp0").Value);
        // status
        Assert.Equal("running", d.Status);
        Assert.Equal(0.0123, d.CpuFraction);
        Assert.Equal(123456789L, d.MemUsedBytes);
        Assert.Equal(2147483648L, d.MemMaxBytes);
        Assert.Equal(1000L, d.DiskUsedBytes);
        Assert.Equal(8589934592L, d.DiskMaxBytes);
        Assert.Equal(3600L, d.UptimeSeconds);
    }

    [Fact]
    public async Task GetLxcDetail_StatusCallFails_StillReturnsConfig()
    {
        // Config succeeds; status/current 500s — status is best-effort enrichment.
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/status/current")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(ConfigJson));

        var d = await client.GetLxcDetailAsync(Profile(), 101);

        Assert.Equal(2, d.Cores);            // config still parsed
        Assert.Equal("unknown", d.Status);   // status defaulted
        Assert.Null(d.CpuFraction);
    }

    [Fact]
    public async Task GetLxcDetail_BuildsNodeAndVmidScopedUrls_WithTokenAuth()
    {
        var paths = new List<string>();
        string? auth = null;
        var client = BuildClient(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            auth = req.Headers.Authorization?.ToString();
            return ByPath()(req);
        });

        await client.GetLxcDetailAsync(Profile(), 101);

        Assert.Contains("/api2/json/nodes/pve/lxc/101/config", paths);
        Assert.Contains("/api2/json/nodes/pve/lxc/101/status/current", paths);
        Assert.Equal("PVEAPIToken root@pam!stash=secret", auth);
    }

    // ── V6.3 — GetLxcRrdDataAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetLxcRrdData_ParsesPoints_AndSkipsRowsWithoutTime()
    {
        const string rrd = """
            {"data":[
              {"time":1000,"cpu":0.5,"mem":100,"maxmem":200,"netin":10,"netout":20,"diskread":30,"diskwrite":40},
              {"cpu":0.6},
              {"time":2000,"cpu":0.7,"mem":150,"maxmem":200,"netin":11,"netout":21,"diskread":31,"diskwrite":41}
            ]}
            """;
        var client = BuildClient(_ => Json(rrd));

        var points = await client.GetLxcRrdDataAsync(Profile(), 101, "hour");

        Assert.Equal(2, points.Count);                  // the time-less row is dropped
        Assert.Equal(1000, points[0].Time);
        Assert.Equal(0.5, points[0].Cpu);
        Assert.Equal(100, points[0].MemUsed);
        Assert.Equal(10, points[0].NetIn);
        Assert.Equal(40, points[0].DiskWrite);
        Assert.Equal(2000, points[1].Time);
    }

    [Theory]
    [InlineData("day", "timeframe=day")]
    [InlineData("week", "timeframe=week")]
    [InlineData("bogus", "timeframe=hour")]   // unknown timeframe falls back to hour
    public async Task GetLxcRrdData_WhitelistsTimeframe(string requested, string expectedQuery)
    {
        string? query = null;
        var client = BuildClient(req => { query = req.RequestUri!.Query; return Json("""{"data":[]}"""); });

        await client.GetLxcRrdDataAsync(Profile(), 101, requested);

        Assert.Contains(expectedQuery, query);
        Assert.Contains("cf=AVERAGE", query);
    }

    [Fact]
    public async Task GetLxcRrdData_NoData_ReturnsEmpty()
    {
        var client = BuildClient(_ => Json("""{"data":"oops"}"""));
        Assert.Empty(await client.GetLxcRrdDataAsync(Profile(), 101, "hour"));
    }

    // ── V6.3 — GetLxcTasksAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetLxcTasks_ParsesTasks_SkipsRowsWithoutUpid_AndDefaultsType()
    {
        const string tasks = """
            {"data":[
              {"upid":"UPID:pve:1:vzstart:101:root@pam:","type":"vzstart","status":"OK","user":"root@pam","starttime":1000,"endtime":1005},
              {"type":"orphan-no-upid"},
              {"upid":"UPID:pve:2:vzshutdown:101:root@pam:","starttime":2000}
            ]}
            """;
        string? query = null;
        var client = BuildClient(req => { query = req.RequestUri!.Query; return Json(tasks); });

        var result = await client.GetLxcTasksAsync(Profile(), 101, 25);

        Assert.Contains("vmid=101", query);
        Assert.Equal(2, result.Count);                 // the upid-less row is dropped
        Assert.Equal("vzstart", result[0].Type);
        Assert.Equal("OK", result[0].Status);
        Assert.Equal("root@pam", result[0].User);
        Assert.Equal(1000, result[0].StartTime);
        Assert.Equal(1005, result[0].EndTime);
        Assert.Equal("task", result[1].Type);          // missing type → default
        Assert.Null(result[1].Status);                 // still running
        Assert.Null(result[1].EndTime);
    }

    [Fact]
    public async Task GetLxcTasks_NoData_ReturnsEmpty()
    {
        var client = BuildClient(_ => Json("""{"foo":1}"""));
        Assert.Empty(await client.GetLxcTasksAsync(Profile(), 101, 25));
    }

    // ── V6.3 — GetTaskLogAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetTaskLog_JoinsLinesWithNewlines()
    {
        var client = BuildClient(_ => Json("""{"data":[{"n":1,"t":"line one"},{"n":2,"t":"line two"}]}"""));

        var log = await client.GetTaskLogAsync(Profile(), "UPID:pve:1:vzstart:101:root@pam:", 512);

        Assert.Equal("line one\nline two", log);
    }

    [Fact]
    public async Task GetTaskLog_PercentEncodesUpidInPath()
    {
        string? uri = null;
        var client = BuildClient(req => { uri = req.RequestUri!.AbsoluteUri; return Json("""{"data":[]}"""); });

        await client.GetTaskLogAsync(Profile(), "UPID:pve:1:vzstart:101:root@pam:", 512);

        Assert.NotNull(uri);
        Assert.Contains("UPID%3Apve", uri);          // colons escaped, not raw in the path
        Assert.DoesNotContain("UPID:pve", uri!);
    }

    [Fact]
    public async Task GetTaskLog_NoData_ReturnsEmptyString()
    {
        var client = BuildClient(_ => Json("""{"data":"nope"}"""));
        Assert.Equal(string.Empty, await client.GetTaskLogAsync(Profile(), "UPID:x:", 512));
    }

    // ── V6.4 — GetLxcStatusAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetLxcStatus_ParsesLiveFields()
    {
        const string json = """
            {"data":{"status":"running","cpu":0.25,"mem":111,"maxmem":222,
            "disk":333,"maxdisk":444,"netin":555,"netout":666,
            "diskread":888,"diskwrite":999,"uptime":777}}
            """;
        var client = BuildClient(_ => Json(json));

        var s = await client.GetLxcStatusAsync(Profile(), 101);

        Assert.Equal("running", s.Status);
        Assert.Equal(0.25, s.Cpu);
        Assert.Equal(111L, s.MemUsed);
        Assert.Equal(222L, s.MemMax);
        Assert.Equal(333L, s.DiskUsed);
        Assert.Equal(444L, s.DiskMax);
        Assert.Equal(555L, s.NetIn);
        Assert.Equal(666L, s.NetOut);
        Assert.Equal(888L, s.DiskRead);
        Assert.Equal(999L, s.DiskWrite);
        Assert.Equal(777L, s.UptimeSeconds);
    }

    [Fact]
    public async Task GetLxcStatus_NoData_ReturnsUnknown()
    {
        var client = BuildClient(_ => Json("""{"data":"oops"}"""));
        var s = await client.GetLxcStatusAsync(Profile(), 101);
        Assert.Equal("unknown", s.Status);
        Assert.Null(s.Cpu);
    }

    // ── V6.4 — LxcStatusActionAsync ──────────────────────────────────────────

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("shutdown")]
    [InlineData("reboot")]
    public async Task LxcStatusAction_PostsToActionUrl(string action)
    {
        HttpMethod? method = null;
        string? path = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.LxcStatusActionAsync(Profile(), 101, action);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"/api2/json/nodes/pve/lxc/101/status/{action}", path);
    }

    [Fact]
    public async Task LxcStatusAction_InvalidAction_ThrowsWithoutCall()
    {
        var calls = 0;
        var client = BuildClient(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.LxcStatusActionAsync(Profile(), 101, "destroy"));
        Assert.Equal(0, calls);
    }

    // ── V6.14 — QEMU VM surface (list / status / rrd / action / config) ───────

    [Fact]
    public async Task ListQemu_ParsesVms_FromQemuEndpoint()
    {
        string? path = null;
        var client = BuildClient(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Json("""
                {"data":[
                  {"vmid":200,"name":"win11","status":"running","uptime":7200,"cpus":4,"maxmem":8589934592,"maxdisk":137438953472,"tags":"vm;prod"},
                  {"vmid":201,"status":"stopped","cpus":2,"maxmem":2147483648}
                ]}
                """);
        });

        var vms = await client.ListQemuAsync(Profile());

        Assert.Equal("/api2/json/nodes/pve/qemu", path);
        Assert.Equal(2, vms.Count);
        var win = vms.Single(v => v.VmId == 200);
        Assert.Equal("win11", win.Name);
        Assert.True(win.IsRunning);
        Assert.Equal(7200, win.UptimeSeconds);
        Assert.Equal(4, win.CpuCores);
        Assert.Equal(8589934592L, win.MemoryBytes);
        Assert.Equal("vm;prod", win.Tags);
        // An unnamed VM falls back to a "vm<vmid>" stem.
        Assert.Equal("vm201", vms.Single(v => v.VmId == 201).Name);
        Assert.False(vms.Single(v => v.VmId == 201).IsRunning);
    }

    [Fact]
    public async Task GetQemuStatus_HitsQemuPath_AndParsesLiveFields()
    {
        string? path = null;
        var client = BuildClient(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"data":{"status":"running","cpu":0.5,"mem":111,"maxmem":222,"uptime":777}}""");
        });

        var s = await client.GetQemuStatusAsync(Profile(), 200);

        Assert.Equal("/api2/json/nodes/pve/qemu/200/status/current", path);
        Assert.Equal("running", s.Status);
        Assert.Equal(0.5, s.Cpu);
        Assert.Equal(777L, s.UptimeSeconds);
    }

    [Fact]
    public async Task GetQemuRrdData_HitsQemuPath_AndWhitelistsTimeframe()
    {
        string? path = null, query = null;
        var client = BuildClient(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            query = req.RequestUri!.Query;
            return Json("""{"data":[{"time":1000,"cpu":0.5,"mem":100,"maxmem":200}]}""");
        });

        var points = await client.GetQemuRrdDataAsync(Profile(), 200, "bogus");

        Assert.Equal("/api2/json/nodes/pve/qemu/200/rrddata", path);
        Assert.Contains("timeframe=hour", query);   // unknown → hour
        Assert.Single(points);
        Assert.Equal(1000, points[0].Time);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("shutdown")]
    [InlineData("reboot")]
    public async Task QemuStatusAction_PostsToQemuActionUrl(string action)
    {
        HttpMethod? method = null;
        string? path = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.QemuStatusActionAsync(Profile(), 200, action);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"/api2/json/nodes/pve/qemu/200/status/{action}", path);
    }

    [Fact]
    public async Task QemuStatusAction_InvalidAction_ThrowsWithoutCall()
    {
        var calls = 0;
        var client = BuildClient(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        await Assert.ThrowsAsync<ArgumentException>(() => client.QemuStatusActionAsync(Profile(), 200, "migrate"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GetQemuDetail_ParsesConfig_MapsDisksAndNics_ComputesTotalCores()
    {
        const string qemuConfig = """
            {"data":{"cores":2,"sockets":2,"memory":4096,"name":"win11","ostype":"win11","onboot":1,
            "scsi0":"local-lvm:vm-200-disk-0,size=64G","virtio0":"local-lvm:vm-200-disk-1,size=32G",
            "net0":"virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0","ide2":"none,media=cdrom",
            "smbios1":"uuid=abc","digest":"x"}}
            """;
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/status/current")
                ? Json("""{"data":{"status":"running","cpu":0.1,"mem":10,"maxmem":20,"uptime":50}}""")
                : Json(qemuConfig));

        var d = await client.GetQemuDetailAsync(Profile(), 200);

        Assert.Equal(4, d.Cores);                       // cores 2 × sockets 2
        Assert.Equal(4096L * 1024 * 1024, d.MemoryBytes); // MiB → bytes
        Assert.Equal("win11", d.Hostname);              // VM name → hostname slot
        Assert.Equal("win11", d.OsType);
        Assert.True(d.Onboot);
        // LXC-only fields stay null for a VM.
        Assert.Null(d.SwapBytes);
        Assert.Null(d.Unprivileged);
        Assert.Null(d.Features);
        // Disks land under Mounts; NICs under Networks.
        Assert.Equal(["ide2", "scsi0", "virtio0"], d.Mounts.Select(m => m.Key));   // ordinal sort
        Assert.Equal(["net0"], d.Networks.Select(n => n.Key));
        // status
        Assert.Equal("running", d.Status);
        Assert.Equal(0.1, d.CpuFraction);
        Assert.Equal(50L, d.UptimeSeconds);
    }

    [Fact]
    public async Task GetQemuDetail_BuildsQemuScopedUrls()
    {
        var paths = new List<string>();
        var client = BuildClient(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri!.AbsolutePath.EndsWith("/status/current")
                ? Json("""{"data":{"status":"running"}}""")
                : Json("""{"data":{"cores":1,"memory":1024}}""");
        });

        await client.GetQemuDetailAsync(Profile(), 200);

        Assert.Contains("/api2/json/nodes/pve/qemu/200/config", paths);
        Assert.Contains("/api2/json/nodes/pve/qemu/200/status/current", paths);
    }

    // ── V6.13 — DeleteLxcAsync ───────────────────────────────────────────────

    [Fact]
    public async Task DeleteLxc_DeletesToLxcUrl()
    {
        HttpMethod? method = null;
        string? path = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.DeleteLxcAsync(Profile(), 101);

        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("/api2/json/nodes/pve/lxc/101", path);
    }

    [Fact]
    public async Task DeleteQemu_DeletesToQemuUrl()
    {
        HttpMethod? method = null;
        string? path = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.DeleteQemuAsync(Profile(), 200);

        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("/api2/json/nodes/pve/qemu/200", path);
    }

    [Fact]
    public async Task DeleteQemu_NonSuccess_SurfacesHostMessage()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("VM 200 is running - stop it before destroying"),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteQemuAsync(Profile(), 200));
        Assert.Contains("VM 200 is running", ex.Message);
    }

    [Fact]
    public async Task DeleteLxc_NonSuccess_SurfacesHostMessage()
    {
        // A 500 "CT is running" must surface the host's message verbatim, not a
        // bare status code, so the controller can relay it.
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("CT 101 is running - stop it before destroying"),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteLxcAsync(Profile(), 101));
        Assert.Contains("CT 101 is running", ex.Message);
    }

    // ── V6.13.1 — CreateLxcAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreateLxc_PostsExpectedFormBody_ThenPollsTaskToTerminal()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:lxc-create::root@pam:"}""");
        });

        await client.CreateLxcAsync(Profile(), new ProxmoxLxcCreate(
            VmId: 150,
            OsTemplate: "local:vztmpl/debian-12.tar.zst",
            RootfsStorage: "local-lvm",
            RootfsSizeGib: 8,
            Hostname: "newct",
            Cores: 2,
            MemoryMib: 1024,
            SwapMib: 512,
            Net0: new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Ip: "dhcp"),
            Unprivileged: true,
            Onboot: true,
            Start: true,
            Password: "s3cret"));

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api2/json/nodes/pve/lxc", path);
        Assert.NotNull(body);
        Assert.Contains("vmid=150", body);
        Assert.Contains("ostemplate=local", body);
        Assert.Contains("rootfs=local-lvm%3A8", body);   // storage:size, url-encoded ':'
        Assert.Contains("hostname=newct", body);
        Assert.Contains("cores=2", body);
        Assert.Contains("memory=1024", body);
        Assert.Contains("swap=512", body);
        Assert.Contains("unprivileged=1", body);
        Assert.Contains("onboot=1", body);
        Assert.Contains("start=1", body);
        Assert.Contains("password=s3cret", body);
        Assert.Contains("net0=", body);
        Assert.True(statusPolls >= 1);   // polled the task to a terminal state
    }

    [Fact]
    public async Task CreateLxc_EmitsFeaturesAndDns_WhenSet()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:lxc-create::root@pam:"}""");
        });

        await client.CreateLxcAsync(Profile(), new ProxmoxLxcCreate(
            150, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8,
            Nesting: true, Nameserver: "1.1.1.1", SearchDomain: "lan"));

        Assert.NotNull(body);
        Assert.Contains("features=nesting%3D1", body);   // nesting=1, url-encoded '='
        Assert.Contains("nameserver=1.1.1.1", body);
        Assert.Contains("searchdomain=lan", body);
    }

    [Fact]
    public async Task AddHaResource_PostsSidToHaResources()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":null}""");
        });

        await client.AddHaResourceAsync(Profile(), 150);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api2/json/cluster/ha/resources", path);
        Assert.Contains("sid=ct%3A150", body);   // ct:150, url-encoded ':'
    }

    [Fact]
    public async Task CreateLxc_PostNonSuccess_SurfacesHostMessage()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("vmid 150 already exists"),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateLxcAsync(
            Profile(), new ProxmoxLxcCreate(150, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8)));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task CreateLxc_TaskFails_SurfacesExitStatus()
    {
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/status")
                ? Json("""{"data":{"status":"stopped","exitstatus":"unable to create CT 150 - no space left"}}""")
                : Json("""{"data":"UPID:pve:0000:lxc-create::root@pam:"}"""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateLxcAsync(
            Profile(), new ProxmoxLxcCreate(150, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8)));
        Assert.Contains("no space left", ex.Message);
    }

    [Fact]
    public async Task GetNextVmId_ParsesStringEncodedId()
    {
        var client = BuildClient(req =>
        {
            Assert.Equal("/api2/json/cluster/nextid", req.RequestUri!.AbsolutePath);
            return Json("""{"data":"123"}""");
        });

        Assert.Equal(123, await client.GetNextVmIdAsync(Profile()));
    }

    [Fact]
    public async Task ListTemplates_ReadsVztmplStoragesOnly()
    {
        var client = BuildClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/storage"))
                return Json("""
                    {"data":[
                      {"storage":"local","content":"vztmpl,iso","enabled":1,"active":1},
                      {"storage":"local-lvm","content":"rootdir,images","enabled":1,"active":1}
                    ]}
                    """);
            // Only the vztmpl-capable storage's content is requested.
            Assert.Contains("/storage/local/content", path);
            return Json("""
                {"data":[
                  {"volid":"local:vztmpl/debian-12.tar.zst","size":123456},
                  {"volid":"local:vztmpl/alpine-3.tar.zst","size":654321}
                ]}
                """);
        });

        var templates = await client.ListTemplatesAsync(Profile());

        Assert.Equal(2, templates.Count);
        Assert.All(templates, t => Assert.Equal("local", t.Storage));
        Assert.Contains(templates, t => t.Volid == "local:vztmpl/debian-12.tar.zst");
    }

    // ── V8.1 — restore from backup ───────────────────────────────────────────

    [Fact]
    public async Task RestoreLxc_PostsRestoreFlag_NoRootfsOrSecrets_ThenPollsTask()
    {
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:lxc-restore::root@pam:"}""");
        });

        await client.CreateLxcAsync(Profile(), new ProxmoxLxcCreate(
            VmId: 150,
            OsTemplate: "local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst",
            RootfsStorage: "local-lvm",
            RootfsSizeGib: 0,
            Unprivileged: true,
            Start: true,
            Restore: true));

        Assert.Equal("/api2/json/nodes/pve/lxc", path);
        Assert.NotNull(body);
        Assert.Contains("vmid=150", body);
        Assert.Contains("ostemplate=local%3Abackup", body);   // the archive volid
        Assert.Contains("restore=1", body);
        Assert.Contains("storage=local-lvm", body);            // the default-storage override
        Assert.DoesNotContain("rootfs=", body);                // sizes come from the archive
        Assert.DoesNotContain("force=", body);                 // not an overwrite
        Assert.DoesNotContain("password=", body);              // no template-only fields
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task RestoreLxc_Overwrite_EmitsForceFlag()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:lxc-restore::root@pam:"}""");
        });

        await client.CreateLxcAsync(Profile(), new ProxmoxLxcCreate(
            150, "local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst", "", 0,
            Restore: true, Force: true));

        Assert.NotNull(body);
        Assert.Contains("restore=1", body);
        Assert.Contains("force=1", body);
        Assert.DoesNotContain("storage=", body);   // blank override ⇒ not sent
    }

    [Fact]
    public async Task ListBackups_ReadsBackupStoragesOnly_FiltersLxcArchives()
    {
        var client = BuildClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/storage"))
                return Json("""
                    {"data":[
                      {"storage":"local","content":"vztmpl,backup,iso","type":"dir","enabled":1,"active":1},
                      {"storage":"local-lvm","content":"rootdir,images","type":"lvmthin","enabled":1,"active":1},
                      {"storage":"pbs-store","content":"backup","type":"pbs","enabled":1,"active":1}
                    ]}
                    """);
            // Only the backup-capable, non-PBS storage's content is requested.
            Assert.Contains("/storage/local/content", path);
            return Json("""
                {"data":[
                  {"volid":"local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst","ctime":1767225600,"size":12345,"format":"tar.zst","subtype":"lxc","vmid":101},
                  {"volid":"local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst","ctime":1767225600,"size":999,"format":"vma.zst","subtype":"qemu","vmid":200}
                ]}
                """);
        });

        var backups = await client.ListBackupsAsync(Profile());

        var only = Assert.Single(backups);   // the qemu archive + the PBS store are excluded
        Assert.Equal("local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst", only.Volid);
        Assert.Equal(101, only.VmId);
        Assert.Equal("local", only.Storage);
        Assert.Equal("tar.zst", only.Format);
    }

    // ── V8.3 — restore VM from backup ─────────────────────────────────────────

    [Fact]
    public async Task RestoreQemu_PostsArchive_NoLxcFields_ThenPollsTask()
    {
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:qemu-restore::root@pam:"}""");
        });

        await client.RestoreQemuAsync(Profile(), new ProxmoxLxcCreate(
            VmId: 250,
            OsTemplate: "local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst",
            RootfsStorage: "local-lvm",
            RootfsSizeGib: 0,
            Hostname: "web-restored",
            Start: true,
            Restore: true));

        Assert.Equal("/api2/json/nodes/pve/qemu", path);     // the QEMU create endpoint
        Assert.NotNull(body);
        Assert.Contains("vmid=250", body);
        Assert.Contains("archive=local%3Abackup", body);     // archive= (not ostemplate=)
        Assert.Contains("storage=local-lvm", body);          // the default-storage override
        Assert.Contains("name=web-restored", body);          // VM name rides in name=
        Assert.DoesNotContain("ostemplate=", body);          // LXC-only
        Assert.DoesNotContain("restore=1", body);            // LXC-only flag
        Assert.DoesNotContain("force=", body);               // not an overwrite
        Assert.DoesNotContain("unprivileged=", body);        // LXC-only
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task RestoreQemu_Overwrite_EmitsForceFlag()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:qemu-restore::root@pam:"}""");
        });

        await client.RestoreQemuAsync(Profile(), new ProxmoxLxcCreate(
            250, "local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst", "", 0,
            Restore: true, Force: true));

        Assert.NotNull(body);
        Assert.Contains("archive=local%3Abackup", body);
        Assert.Contains("force=1", body);
        Assert.DoesNotContain("storage=", body);   // blank override ⇒ not sent
    }

    [Fact]
    public async Task ListBackups_Qemu_ReadsBackupStoragesOnly_FiltersQemuArchives()
    {
        var client = BuildClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/storage"))
                return Json("""
                    {"data":[
                      {"storage":"local","content":"vztmpl,backup,iso","type":"dir","enabled":1,"active":1},
                      {"storage":"pbs-store","content":"backup","type":"pbs","enabled":1,"active":1}
                    ]}
                    """);
            Assert.Contains("/storage/local/content", path);
            return Json("""
                {"data":[
                  {"volid":"local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst","ctime":1767225600,"size":12345,"format":"tar.zst","subtype":"lxc","vmid":101},
                  {"volid":"local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst","ctime":1767312000,"size":999,"format":"vma.zst","subtype":"qemu","vmid":200}
                ]}
                """);
        });

        var backups = await client.ListBackupsAsync(Profile(), qemu: true);

        var only = Assert.Single(backups);   // the lxc archive + the PBS store are excluded
        Assert.Equal("local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst", only.Volid);
        Assert.Equal(200, only.VmId);
        Assert.Equal("vma.zst", only.Format);
    }

    // ── V8.0 — clone & snapshot ──────────────────────────────────────────────

    [Fact]
    public async Task CloneLxc_PostsExpectedFormBody_ThenPollsTaskToTerminal()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:lxc-clone::root@pam:"}""");
        });

        await client.CloneLxcAsync(Profile(), sourceVmId: 105, new ProxmoxLxcClone(
            NewVmId: 160, Hostname: "newct-clone", TargetStorage: "local-lvm", Full: true, SnapName: "before-upgrade"));

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api2/json/nodes/pve/lxc/105/clone", path);
        Assert.NotNull(body);
        Assert.Contains("newid=160", body);
        Assert.Contains("full=1", body);
        Assert.Contains("hostname=newct-clone", body);
        Assert.Contains("storage=local-lvm", body);
        Assert.Contains("snapname=before-upgrade", body);
        Assert.True(statusPolls >= 1);   // polled the task to a terminal state
    }

    [Fact]
    public async Task CloneLxc_TaskFails_SurfacesExitStatus()
    {
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/status")
                ? Json("""{"data":{"status":"stopped","exitstatus":"clone failed: no space left"}}""")
                : Json("""{"data":"UPID:pve:0000:lxc-clone::root@pam:"}"""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CloneLxcAsync(
            Profile(), 105, new ProxmoxLxcClone(160)));
        Assert.Contains("no space left", ex.Message);
    }

    [Fact]
    public async Task CreateSnapshot_PostsExpectedFormBody_ThenPollsTask()
    {
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:vzsnapshot::root@pam:"}""");
        });

        await client.CreateLxcSnapshotAsync(Profile(), 105, "before-upgrade", "pre");

        Assert.Equal("/api2/json/nodes/pve/lxc/105/snapshot", path);
        Assert.NotNull(body);
        Assert.Contains("snapname=before-upgrade", body);
        Assert.Contains("description=pre", body);
        // The LXC snapshot endpoint has no vmstate option — it must never be sent.
        Assert.DoesNotContain("vmstate", body);
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task RollbackSnapshot_PostsToRollbackPath_ThenPollsTask()
    {
        HttpMethod? method = null;
        string? path = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"data":"UPID:pve:0000:vzrollback::root@pam:"}""");
        });

        await client.RollbackLxcSnapshotAsync(Profile(), 105, "before-upgrade");

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api2/json/nodes/pve/lxc/105/snapshot/before-upgrade/rollback", path);
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task DeleteSnapshot_DeletesSnapshotPath_ThenPollsTask()
    {
        HttpMethod? method = null;
        string? path = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"data":"UPID:pve:0000:vzdelsnapshot::root@pam:"}""");
        });

        await client.DeleteLxcSnapshotAsync(Profile(), 105, "before-upgrade");

        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("/api2/json/nodes/pve/lxc/105/snapshot/before-upgrade", path);
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task ListSnapshots_FiltersOutCurrentPseudoEntry_AndSortsNewestFirst()
    {
        var client = BuildClient(_ => Json("""
            {"data":[
              {"name":"current","digest":"abc"},
              {"name":"snap-old","snaptime":1700000000,"description":"old"},
              {"name":"snap-new","snaptime":1700009999,"vmstate":1,"parent":"snap-old"}
            ]}
            """));

        var snapshots = await client.ListLxcSnapshotsAsync(Profile(), 105);

        Assert.Equal(2, snapshots.Count);
        Assert.DoesNotContain(snapshots, s => s.Name == "current");
        Assert.Equal("snap-new", snapshots[0].Name);   // newest first
        Assert.True(snapshots[0].Vmstate);
        Assert.Equal("snap-old", snapshots[0].Parent);
    }

    [Fact]
    public async Task DeleteSnapshot_NonSuccess_SurfacesHostMessage()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("snapshot 'before-upgrade' does not exist"),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteLxcSnapshotAsync(
            Profile(), 105, "before-upgrade"));
        Assert.Contains("does not exist", ex.Message);
    }

    // ── V8.2 — clone & snapshot QEMU VMs ─────────────────────────────────────

    [Fact]
    public async Task CloneQemu_PostsNameAndFormat_ToQemuClonePath_ThenPollsTask()
    {
        string? path = null;
        string? body = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:qmclone::root@pam:"}""");
        });

        await client.CloneQemuAsync(Profile(), sourceVmId: 200, new ProxmoxLxcClone(
            NewVmId: 260, Hostname: "newvm-clone", TargetStorage: "local-lvm", Full: true, Format: "qcow2"));

        Assert.Equal("/api2/json/nodes/pve/qemu/200/clone", path);
        Assert.NotNull(body);
        Assert.Contains("newid=260", body);
        // A VM sends the new name as name=, not hostname=.
        Assert.Contains("name=newvm-clone", body);
        Assert.DoesNotContain("hostname=", body);
        Assert.Contains("format=qcow2", body);
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task CreateQemuSnapshot_WithVmstate_SendsVmstate_ToQemuPath()
    {
        string? path = null;
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:qmsnapshot::root@pam:"}""");
        });

        await client.CreateQemuSnapshotAsync(Profile(), 200, "before-upgrade", null, vmstate: true);

        Assert.Equal("/api2/json/nodes/pve/qemu/200/snapshot", path);
        Assert.NotNull(body);
        Assert.Contains("snapname=before-upgrade", body);
        Assert.Contains("vmstate=1", body);
    }

    [Fact]
    public async Task CreateQemuSnapshot_WithoutVmstate_OmitsVmstate()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":"UPID:pve:0000:qmsnapshot::root@pam:"}""");
        });

        await client.CreateQemuSnapshotAsync(Profile(), 200, "before-upgrade", null, vmstate: false);

        Assert.NotNull(body);
        Assert.DoesNotContain("vmstate", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RollbackAndDeleteQemuSnapshot_HitQemuPaths_ThenPollTask(bool rollback)
    {
        HttpMethod? method = null;
        string? path = null;
        var statusPolls = 0;
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/status"))
            {
                statusPolls++;
                return Json("""{"data":{"status":"stopped","exitstatus":"OK"}}""");
            }
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"data":"UPID:pve:0000:qmsnap::root@pam:"}""");
        });

        if (rollback) await client.RollbackQemuSnapshotAsync(Profile(), 200, "before-upgrade");
        else await client.DeleteQemuSnapshotAsync(Profile(), 200, "before-upgrade");

        Assert.Equal(rollback ? HttpMethod.Post : HttpMethod.Delete, method);
        Assert.Equal(
            rollback
                ? "/api2/json/nodes/pve/qemu/200/snapshot/before-upgrade/rollback"
                : "/api2/json/nodes/pve/qemu/200/snapshot/before-upgrade",
            path);
        Assert.True(statusPolls >= 1);
    }

    [Fact]
    public async Task ListQemuSnapshots_HitsQemuPath()
    {
        string? path = null;
        var client = BuildClient(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return Json("""{"data":[{"name":"snap1","snaptime":1700000000,"vmstate":1}]}""");
        });

        var snapshots = await client.ListQemuSnapshotsAsync(Profile(), 200);

        Assert.Equal("/api2/json/nodes/pve/qemu/200/snapshot", path);
        Assert.Single(snapshots);
        Assert.True(snapshots[0].Vmstate);
    }

    // ── V6.5 — UpdateLxcConfigAsync ──────────────────────────────────────────

    [Fact]
    public async Task UpdateLxcConfig_PutsScalarFields_AsFormBody()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        var client = BuildClient(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(
            Profile(), 101,
            new ProxmoxLxcConfigUpdate(Cores: 4, MemoryMib: 4096, SwapMib: 1024, Hostname: "router", Onboot: true));

        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/api2/json/nodes/pve/lxc/101/config", path);
        Assert.NotNull(body);
        Assert.Contains("cores=4", body);
        Assert.Contains("memory=4096", body);   // MiB, not bytes
        Assert.Contains("swap=1024", body);
        Assert.Contains("hostname=router", body);
        Assert.Contains("onboot=1", body);       // bool → 1/0
    }

    [Fact]
    public async Task UpdateLxcConfig_OmitsNullFields()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Only memory changed; everything else is left untouched.
        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(MemoryMib: 2048));

        Assert.Equal("memory=2048", body);
        Assert.DoesNotContain("cores", body!);
        Assert.DoesNotContain("hostname", body!);
        Assert.DoesNotContain("onboot", body!);
    }

    [Fact]
    public async Task UpdateLxcConfig_OnbootFalse_SendsZero()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(Onboot: false));

        Assert.Equal("onboot=0", body);
    }

    [Fact]
    public async Task UpdateLxcConfig_NothingToChange_MakesNoCall()
    {
        var calls = 0;
        var client = BuildClient(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate());

        Assert.Equal(0, calls);   // empty update never touches the host
    }

    [Fact]
    public async Task UpdateLxcConfig_NonSuccess_ThrowsWithProxmoxErrorBody()
    {
        // A 403 from a token missing VM.Config.* must surface the host's message,
        // not a bare status code.
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Permission check failed (/vms/101, VM.Config.Memory)"),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(MemoryMib: 4096)));

        Assert.Contains("403", ex.Message);
        Assert.Contains("VM.Config.Memory", ex.Message);
    }

    // ── V6.9 — structured net / mp / rootfs writes ───────────────────────────

    [Fact]
    public async Task UpdateLxcConfig_UpdatesExistingInterface_FormatsLineUnderItsKey()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put)
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Ip: "dhcp", Firewall: true)]));

        Assert.NotNull(body);
        var decoded = Uri.UnescapeDataString(body!);
        Assert.Contains("net0=", decoded);
        Assert.Contains("name=eth0", decoded);
        Assert.Contains("bridge=vmbr0", decoded);
        Assert.Contains("firewall=1", decoded);
    }

    [Fact]
    public async Task UpdateLxcConfig_AddInterface_AssignsNextFreeKey_FromCurrentConfig()
    {
        // Current config (ByPath) already has net0 + net1 → an add must become net2,
        // proving the server — not the client — owns key numbering.
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return ByPath()(req);   // GET config / status for key seeding
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("", Name: "eth2", Bridge: "vmbr2", Ip: "dhcp")]));

        Assert.NotNull(body);
        Assert.Contains("net2=", Uri.UnescapeDataString(body!));
    }

    [Fact]
    public async Task UpdateLxcConfig_AddMount_AssignsNextFreeKey()
    {
        // Current config has mp0 → an add must become mp1.
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return ByPath()(req);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Mounts: [new ProxmoxLxcMountChange("", Volume: "local-lvm:8", MountPoint: "/extra", Size: "8G")]));

        Assert.NotNull(body);
        var decoded = Uri.UnescapeDataString(body!);
        Assert.Contains("mp1=", decoded);
        Assert.Contains("mp=/extra", decoded);
        Assert.Contains("size=8G", decoded);
    }

    [Fact]
    public async Task UpdateLxcConfig_Removals_EmitSingleDeleteList()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put)
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net1", Remove: true)],
            Mounts: [new ProxmoxLxcMountChange("mp2", Remove: true)]));

        Assert.NotNull(body);
        var decoded = Uri.UnescapeDataString(body!);
        Assert.Contains("delete=", decoded);
        Assert.Contains("net1", decoded);
        Assert.Contains("mp2", decoded);
    }

    [Fact]
    public async Task UpdateLxcConfig_RemoveOnly_DoesNotReadConfigForKeys()
    {
        // No adds → no GET round-trip; only the PUT happens.
        var methods = new List<HttpMethod>();
        var client = BuildClient(req =>
        {
            methods.Add(req.Method);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Networks: [new ProxmoxLxcNetChange("net1", Remove: true)]));

        Assert.Equal([HttpMethod.Put], methods);
    }

    [Fact]
    public async Task UpdateLxcConfig_Rootfs_WritesUnderRootfsKey()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put)
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            Rootfs: new ProxmoxLxcRootfsChange(Volume: "local-lvm:vm-101-disk-0", Size: "16G")));

        Assert.NotNull(body);
        var decoded = Uri.UnescapeDataString(body!);
        Assert.Contains("rootfs=", decoded);
        Assert.Contains("size=16G", decoded);
    }

    [Fact]
    public async Task UpdateLxcConfig_ScalarAndStructuredTogether_SendBothInOnePut()
    {
        string? body = null;
        var puts = 0;
        var client = BuildClient(req =>
        {
            if (req.Method == HttpMethod.Put) { puts++; body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.UpdateLxcConfigAsync(Profile(), 101, new ProxmoxLxcConfigUpdate(
            MemoryMib: 4096,
            Networks: [new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Ip: "dhcp")]));

        Assert.Equal(1, puts);
        var decoded = Uri.UnescapeDataString(body!);
        Assert.Contains("memory=4096", decoded);
        Assert.Contains("net0=", decoded);
    }

    // ── V6.8 — GetNodeStatusAsync ────────────────────────────────────────────

    private const string NodeStatusJson = """
        {"data":{
          "uptime":864000,"cpu":0.0731,"wait":0.0042,
          "loadavg":["0.50","0.80","1.10"],
          "cpuinfo":{"model":"Intel(R) Core(TM) i5-8500","sockets":1,"cpus":6,"cores":6,"mhz":"3100.00","hvm":"1"},
          "memory":{"total":33567453184,"used":12000000000,"free":21567453184},
          "swap":{"total":8589934592,"used":1024},
          "rootfs":{"total":100000000000,"used":40000000000,"avail":60000000000},
          "kversion":"Linux 6.8.12-pve","pveversion":"pve-manager/8.2.2/abc"
        }}
        """;

    private const string SubscriptionJson =
        """{"data":{"status":"notfound","level":""}}""";

    [Fact]
    public async Task GetNodeStatus_ParsesStatusAndSubscription()
    {
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/subscription") ? Json(SubscriptionJson) : Json(NodeStatusJson));

        var s = await client.GetNodeStatusAsync(Profile());

        Assert.Equal("Intel(R) Core(TM) i5-8500", s.CpuModel);
        Assert.Equal(1, s.Sockets);
        Assert.Equal(6, s.Cpus);
        Assert.Equal(6, s.Cores);
        Assert.Equal(3100.0, s.CpuMhz);
        Assert.True(s.Hvm);
        Assert.Equal(0.0731, s.CpuFraction);
        Assert.Equal(0.0042, s.IoWaitFraction);
        Assert.Equal(0.50, s.Load1);
        Assert.Equal(0.80, s.Load5);
        Assert.Equal(1.10, s.Load15);
        Assert.Equal(33567453184L, s.MemTotal);
        Assert.Equal(12000000000L, s.MemUsed);
        Assert.Equal(8589934592L, s.SwapTotal);
        Assert.Equal(100000000000L, s.RootTotal);
        Assert.Equal(40000000000L, s.RootUsed);
        Assert.Equal(864000L, s.UptimeSeconds);
        Assert.Equal("Linux 6.8.12-pve", s.KernelVersion);
        Assert.Equal("pve-manager/8.2.2/abc", s.PveVersion);
        Assert.Equal("notfound", s.SubscriptionStatus);
    }

    [Fact]
    public async Task GetNodeStatus_SubscriptionCallFails_StillReturnsStatus()
    {
        var client = BuildClient(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/subscription")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(NodeStatusJson));

        var s = await client.GetNodeStatusAsync(Profile());

        Assert.Equal(6, s.Cpus);               // status still parsed
        Assert.Null(s.SubscriptionStatus);     // subscription best-effort
    }

    // ── V6.8 — GetNodeRrdDataAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetNodeRrdData_ParsesPoints_AndSkipsRowsWithoutTime()
    {
        const string rrd = """
            {"data":[
              {"time":1000,"cpu":0.5,"iowait":0.01,"loadavg":0.7,"memtotal":200,"memused":100,"netin":10,"netout":20,"roottotal":500,"rootused":250,"swapused":5},
              {"cpu":0.6},
              {"time":2000,"cpu":0.7,"memused":150}
            ]}
            """;
        string? query = null;
        var client = BuildClient(req => { query = req.RequestUri!.Query; return Json(rrd); });

        var points = await client.GetNodeRrdDataAsync(Profile(), "day");

        Assert.Contains("timeframe=day", query);
        Assert.Contains("cf=AVERAGE", query);
        Assert.Equal(2, points.Count);
        Assert.Equal(1000, points[0].Time);
        Assert.Equal(0.01, points[0].IoWait);
        Assert.Equal(250, points[0].RootUsed);
        Assert.Equal(2000, points[1].Time);
    }

    // ── V6.8 — GetNodeStoragesAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetNodeStorages_ParsesPools_SortsByName_SkipsNameless()
    {
        const string json = """
            {"data":[
              {"storage":"local-lvm","type":"lvmthin","content":"images,rootdir","enabled":1,"active":1,"total":500,"used":200,"avail":300},
              {"type":"dir-no-name"},
              {"storage":"local","type":"dir","content":"iso","enabled":1,"active":1,"total":100,"used":40,"avail":60}
            ]}
            """;
        var client = BuildClient(_ => Json(json));

        var pools = await client.GetNodeStoragesAsync(Profile());

        Assert.Equal(2, pools.Count);                       // nameless row dropped
        Assert.Equal("local", pools[0].Storage);            // ordinal sort
        Assert.Equal("local-lvm", pools[1].Storage);
        Assert.True(pools[1].Active);
        Assert.Equal(200, pools[1].Used);
    }

    // ── V6.8 — GetNodeDisksAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetNodeDisks_ParsesDisks_HealthAndWearout()
    {
        const string json = """
            {"data":[
              {"devpath":"/dev/sda","model":"Samsung SSD 860","serial":"S1","vendor":"ATA","type":"ssd","size":512110190592,"health":"PASSED","wearout":97,"rpm":0,"used":"LVM"},
              {"devpath":"/dev/sdb","model":"WDC","type":"hdd","size":4000787030016,"health":"PASSED","wearout":"N/A","rpm":7200,"used":"ZFS"},
              {"model":"orphan-no-devpath"}
            ]}
            """;
        var client = BuildClient(_ => Json(json));

        var disks = await client.GetNodeDisksAsync(Profile());

        Assert.Equal(2, disks.Count);                       // devpath-less row dropped
        Assert.Equal("/dev/sda", disks[0].DevPath);
        Assert.Equal("PASSED", disks[0].Health);
        Assert.Equal(97, disks[0].WearoutPercent);
        Assert.Equal("ssd", disks[0].Type);
        Assert.Null(disks[1].WearoutPercent);               // "N/A" → null
        Assert.Equal(7200, disks[1].Rpm);
    }

    // ── V6.8 — GetNodeDiskSmartAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetNodeDiskSmart_ParsesAttributes_AndEscapesDiskParam()
    {
        const string json = """
            {"data":{"health":"PASSED","type":"ata","attributes":[
              {"id":5,"name":"Reallocated_Sector_Ct","value":100,"worst":100,"threshold":10,"raw":"0"},
              {"name":"missing-id"},
              {"id":194,"name":"Temperature_Celsius","value":70,"worst":50,"threshold":0,"raw":"30"}
            ]}}
            """;
        string? query = null;
        var client = BuildClient(req => { query = req.RequestUri!.Query; return Json(json); });

        var smart = await client.GetNodeDiskSmartAsync(Profile(), "/dev/sda");

        Assert.Contains("disk=%2Fdev%2Fsda", query);        // slashes escaped
        Assert.Equal("PASSED", smart.Health);
        Assert.Equal("ata", smart.Type);
        Assert.Equal(2, smart.Attributes.Count);            // id-less attr dropped
        Assert.Equal(5, smart.Attributes[0].Id);
        Assert.Equal("Reallocated_Sector_Ct", smart.Attributes[0].Name);
        Assert.Equal(10, smart.Attributes[0].Threshold);
    }

    [Fact]
    public async Task GetNodeDiskSmart_Nvme_ReturnsText()
    {
        const string json = """{"data":{"health":"PASSED","type":"text","text":"SMART overall-health: PASSED"}}""";
        var client = BuildClient(_ => Json(json));

        var smart = await client.GetNodeDiskSmartAsync(Profile(), "/dev/nvme0n1");

        Assert.Empty(smart.Attributes);
        Assert.Equal("SMART overall-health: PASSED", smart.Text);
    }

    // ── V7.2.1 — a per-disk SMART failure is NOT host-unreachable ─────────────

    [Fact]
    public async Task GetNodeDiskSmart_HostReturns400_SurfacesPerDiskMessage_DoesNotThrow()
    {
        // PBS/PVE relay a `smartctl` failure for one disk as a 4xx (e.g. a
        // USB-bridged disk, or the PBS 3.2.9 regression) — the host is reachable,
        // so this must surface as the disk's SMART text, never bubble up as the
        // "Proxmox host unreachable" 502 the shared GetJsonAsync path would cause.
        const string body = """command "smartctl" "-H" "-A" "-j" "/dev/sdb" failed - status code: 4""";
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        var smart = await client.GetNodeDiskSmartAsync(Profile(), "/dev/sdb");

        Assert.Null(smart.Health);
        Assert.Empty(smart.Attributes);
        Assert.NotNull(smart.Text);
        Assert.Contains("/dev/sdb", smart.Text);     // names the affected disk
        Assert.Contains("400", smart.Text);          // carries the host's status
        Assert.Contains("smartctl", smart.Text);     // ... and its real reason
    }

    [Fact]
    public async Task GetNodeDiskSmart_TransportFailure_StillThrows()
    {
        // A genuinely unreachable host (connection refused / no route) must still
        // throw so the controller maps it to a 502 "host unreachable" — only a
        // real HTTP *response* with a 4xx is treated as a per-disk condition.
        var client = BuildClient(_ => throw new HttpRequestException("No route to host (192.168.1.110:8007)"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetNodeDiskSmartAsync(Profile(), "/dev/sda"));
    }

    // ── V6.8 — GetNodeNetworkInterfacesAsync ─────────────────────────────────

    [Fact]
    public async Task GetNodeNetwork_ParsesInterfaces_SortsByName()
    {
        const string json = """
            {"data":[
              {"iface":"vmbr0","type":"bridge","active":1,"autostart":1,"method":"static","cidr":"192.168.1.10/24","gateway":"192.168.1.1","bridge_ports":"eno1"},
              {"type":"orphan-no-iface"},
              {"iface":"eno1","type":"eth","active":1,"autostart":0,"method":"manual"}
            ]}
            """;
        var client = BuildClient(_ => Json(json));

        var ifaces = await client.GetNodeNetworkInterfacesAsync(Profile());

        Assert.Equal(2, ifaces.Count);                      // iface-less row dropped
        Assert.Equal("eno1", ifaces[0].Iface);              // ordinal sort
        Assert.Equal("vmbr0", ifaces[1].Iface);
        Assert.True(ifaces[1].Active);
        Assert.Equal("eno1", ifaces[1].BridgePorts);
        Assert.Equal("192.168.1.1", ifaces[1].Gateway);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>SkipTlsVerify=false so the client resolves the plain "proxmox"
    /// named HttpClient the harness registers.</summary>
    private static ProxmoxConnectionProfile Profile() =>
        new("https://pve.lan:8006", "pve", "root@pam!stash", "secret", SkipTlsVerify: false, Ssh: null);

    /// <summary>Routes the two GetLxcDetail calls to their canned bodies.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> ByPath() =>
        req => req.RequestUri!.AbsolutePath.EndsWith("/status/current") ? Json(StatusJson) : Json(ConfigJson);

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
