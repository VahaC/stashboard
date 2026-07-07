using System.Net.Http.Headers;
using System.Text.Json;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Core.Proxmox;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V6.0 — production <see cref="IProxmoxApiClient"/> over the Proxmox REST API.
/// Authenticates with an API token (no login round-trip, no ticket/CSRF dance):
/// the <c>PVEAPIToken</c> scheme for Proxmox VE, the <c>PBSAPIToken</c> scheme
/// for Proxmox Backup Server (V6.8.3 — the two differ only in the id↔secret
/// separator). Reads JSON defensively with <see cref="JsonDocument"/> because
/// Proxmox is loose about number-vs-string typing (e.g. <c>vmid</c> can come
/// back either way).
/// </summary>
internal sealed class ProxmoxApiClient(IHttpClientFactory httpClientFactory) : IProxmoxApiClient
{
    public const string HttpClientName = "proxmox";
    public const string InsecureHttpClientName = "proxmox-insecure";

    /// <summary>V6.8.3 — the API-token Authorization header for the connection's
    /// product. PVE joins id and secret with <c>=</c>; PBS with <c>:</c>. Using
    /// the wrong scheme is exactly what yields a 401 when a PBS host is added as
    /// PVE (and vice-versa).</summary>
    internal static AuthenticationHeaderValue BuildAuthHeader(ProxmoxConnectionProfile profile) =>
        profile.ServerType == ProxmoxServerType.Pbs
            ? new AuthenticationHeaderValue("PBSAPIToken", $"{profile.ApiTokenId}:{profile.ApiTokenSecret}")
            : new AuthenticationHeaderValue("PVEAPIToken", $"{profile.ApiTokenId}={profile.ApiTokenSecret}");

    public async Task<IReadOnlyList<ProxmoxLxcSummary>> ListLxcAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // PBS has no guests. Probe the node status instead (so a bad token / an
        // unreachable host still surfaces as a connection-level error through the
        // checker's existing try/catch), then return an empty guest list.
        if (profile.ServerType == ProxmoxServerType.Pbs)
        {
            using var _ = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/status", cancellationToken);
            return [];
        }

        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/lxc", cancellationToken);
        return ParseGuestSummaries(doc, namePrefix: "ct");
    }

    public async Task<IReadOnlyList<ProxmoxLxcSummary>> ListQemuAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // PBS has no QEMU guests. Reachability/auth is already validated by the
        // LXC listing the checker runs first, so this is a plain empty list (no
        // extra probe — that would just be a redundant round-trip).
        if (profile.ServerType == ProxmoxServerType.Pbs) return [];

        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/qemu", cancellationToken);
        return ParseGuestSummaries(doc, namePrefix: "vm");
    }

    /// <summary>Parses a <c>GET …/lxc</c> or <c>GET …/qemu</c> list response into
    /// the shared <see cref="ProxmoxLxcSummary"/> shape — both endpoints expose
    /// the same identity + resource fields (<c>vmid</c> / <c>name</c> /
    /// <c>status</c> / <c>uptime</c> / <c>cpus</c> / <c>maxmem</c> / <c>maxdisk</c>
    /// / <c>tags</c>). <paramref name="namePrefix"/> is the fallback name stem for
    /// an unnamed guest (<c>ct</c> for LXC, <c>vm</c> for QEMU).</summary>
    private static List<ProxmoxLxcSummary> ParseGuestSummaries(JsonDocument doc, string namePrefix)
    {
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxLxcSummary>();
        foreach (var item in data.EnumerateArray())
        {
            var vmid = ReadInt(item, "vmid");
            if (vmid is null) continue;
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : $"{namePrefix}{vmid}";
            var isRunning = item.TryGetProperty("status", out var s)
                && s.ValueKind == JsonValueKind.String
                && string.Equals(s.GetString(), "running", StringComparison.OrdinalIgnoreCase);
            // These come free in the same list response — no extra round-trips.
            var tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            result.Add(new ProxmoxLxcSummary(
                vmid.Value, name, isRunning,
                UptimeSeconds: ReadLong(item, "uptime"),
                CpuCores: ReadInt(item, "cpus"),
                MemoryBytes: ReadLong(item, "maxmem"),
                DiskBytes: ReadLong(item, "maxdisk"),
                Tags: string.IsNullOrWhiteSpace(tags) ? null : tags));
        }
        return result;
    }

    public async Task<string?> GetLxcIpAddressAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        try
        {
            // LXC exposes its runtime interfaces over the API (no guest agent
            // needed, unlike QEMU). Pick the first non-loopback IPv4.
            using var doc = await GetJsonAsync(
                profile, $"nodes/{profile.NodeName}/lxc/{vmId}/interfaces", cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var iface in data.EnumerateArray())
            {
                var name = iface.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString()
                    : null;
                if (string.Equals(name, "lo", StringComparison.OrdinalIgnoreCase)) continue;

                if (iface.TryGetProperty("inet", out var inet) && inet.ValueKind == JsonValueKind.String)
                {
                    var raw = inet.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                        return raw.Split('/')[0].Trim(); // strip CIDR suffix
                }
            }
            return null;
        }
        catch
        {
            // Best-effort enrichment — a missing/old endpoint or a transient
            // blip must never turn into a guest-level error.
            return null;
        }
    }

    public async Task<ProxmoxLxcDetail> GetLxcDetailAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        int? cores = null;
        long? memoryBytes = null, swapBytes = null;
        string? hostname = null, osType = null, arch = null, features = null;
        bool? onboot = null, unprivileged = null;
        var networks = new List<ProxmoxLxcConfigLine>();
        var mounts = new List<ProxmoxLxcConfigLine>();

        // ── Static config. The response is a flat object; iterate so we pick up
        //    however many net<n> / mp<n> lines the container happens to have. ──
        using (var cfg = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/lxc/{vmId}/config", cancellationToken))
        {
            if (cfg.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in data.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "cores": cores = ReadInt(data, "cores"); break;
                        case "memory": memoryBytes = MebibytesToBytes(ReadLong(data, "memory")); break;
                        case "swap": swapBytes = MebibytesToBytes(ReadLong(data, "swap")); break;
                        case "hostname": hostname = ReadString(prop.Value); break;
                        case "ostype": osType = ReadString(prop.Value); break;
                        case "arch": arch = ReadString(prop.Value); break;
                        case "features": features = ReadString(prop.Value); break;
                        case "onboot": onboot = ReadBool(data, "onboot"); break;
                        case "unprivileged": unprivileged = ReadBool(data, "unprivileged"); break;
                        case "rootfs": AddLine(mounts, prop); break;
                        default:
                            if (IsIndexed(prop.Name, "mp")) AddLine(mounts, prop);
                            else if (IsIndexed(prop.Name, "net")) AddLine(networks, prop);
                            break;
                    }
                }
            }
        }

        // ── Live status — best-effort; a config read with no status is still
        //    useful (and `status/current` is what fails for a brand-new CT). ──
        var status = "unknown";
        double? cpu = null;
        long? memUsed = null, memMax = null, diskUsed = null, diskMax = null, uptime = null;
        try
        {
            using var st = await GetJsonAsync(
                profile, $"nodes/{profile.NodeName}/lxc/{vmId}/status/current", cancellationToken);
            if (st.RootElement.TryGetProperty("data", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                status = (sd.TryGetProperty("status", out var ss) ? ReadString(ss) : null) ?? "unknown";
                cpu = ReadDouble(sd, "cpu");
                memUsed = ReadLong(sd, "mem");
                memMax = ReadLong(sd, "maxmem");
                diskUsed = ReadLong(sd, "disk");
                diskMax = ReadLong(sd, "maxdisk");
                uptime = ReadLong(sd, "uptime");
            }
        }
        catch
        {
            // Status is enrichment; the config above is the primary payload.
        }

        networks.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        mounts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        return new ProxmoxLxcDetail(
            vmId, status, cores, memoryBytes, swapBytes, hostname, osType, arch, onboot, unprivileged, features,
            networks, mounts, cpu, memUsed, memMax, diskUsed, diskMax, uptime);
    }

    public async Task<ProxmoxLxcDetail> GetQemuDetailAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        int? cores = null, sockets = null;
        long? memoryBytes = null;
        string? name = null, osType = null;
        bool? onboot = null;
        var networks = new List<ProxmoxLxcConfigLine>();
        var disks = new List<ProxmoxLxcConfigLine>();

        // ── Static config. A VM's NICs are net<n> (same as LXC) and its disks are
        //    scsi<n> / virtio<n> / ide<n> / sata<n> / efidisk<n> / tpmstate<n>.
        //    Read-only for now — VM disk/PCI editing is out of scope. ──
        using (var cfg = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/qemu/{vmId}/config", cancellationToken))
        {
            if (cfg.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in data.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "cores": cores = ReadInt(data, "cores"); break;
                        case "sockets": sockets = ReadInt(data, "sockets"); break;
                        case "memory": memoryBytes = MebibytesToBytes(ReadLong(data, "memory")); break;
                        case "name": name = ReadString(prop.Value); break;
                        case "ostype": osType = ReadString(prop.Value); break;
                        case "onboot": onboot = ReadBool(data, "onboot"); break;
                        default:
                            if (IsIndexed(prop.Name, "net")) AddLine(networks, prop);
                            else if (IsDiskKey(prop.Name)) AddLine(disks, prop);
                            break;
                    }
                }
            }
        }

        // QEMU reports cores-per-socket; the user-facing vCPU count is cores ×
        // sockets (sockets defaults to 1 when omitted).
        var totalCores = cores is { } c ? c * (sockets ?? 1) : (int?)null;

        // ── Live status — best-effort, exactly like the LXC path. ──
        var status = "unknown";
        double? cpu = null;
        long? memUsed = null, memMax = null, diskUsed = null, diskMax = null, uptime = null;
        try
        {
            using var st = await GetJsonAsync(
                profile, $"nodes/{profile.NodeName}/qemu/{vmId}/status/current", cancellationToken);
            if (st.RootElement.TryGetProperty("data", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                status = (sd.TryGetProperty("status", out var ss) ? ReadString(ss) : null) ?? "unknown";
                cpu = ReadDouble(sd, "cpu");
                memUsed = ReadLong(sd, "mem");
                memMax = ReadLong(sd, "maxmem");
                diskUsed = ReadLong(sd, "disk");
                diskMax = ReadLong(sd, "maxdisk");
                uptime = ReadLong(sd, "uptime");
            }
        }
        catch
        {
            // Status is enrichment; the config above is the primary payload.
        }

        networks.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        disks.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        // The LXC-only fields (swap / arch / unprivileged / features) don't apply
        // to a VM and stay null; name maps onto the hostname slot the read view
        // already renders.
        return new ProxmoxLxcDetail(
            vmId, status, totalCores, memoryBytes, SwapBytes: null, name, osType, Arch: null,
            onboot, Unprivileged: null, Features: null,
            networks, disks, cpu, memUsed, memMax, diskUsed, diskMax, uptime);
    }

    public Task<IReadOnlyList<ProxmoxLxcRrdPoint>> GetLxcRrdDataAsync(
        ProxmoxConnectionProfile profile, int vmId, string timeframe, CancellationToken cancellationToken = default) =>
        GetGuestRrdDataAsync(profile, "lxc", vmId, timeframe, cancellationToken);

    public Task<IReadOnlyList<ProxmoxLxcRrdPoint>> GetQemuRrdDataAsync(
        ProxmoxConnectionProfile profile, int vmId, string timeframe, CancellationToken cancellationToken = default) =>
        GetGuestRrdDataAsync(profile, "qemu", vmId, timeframe, cancellationToken);

    /// <summary>Shared RRD reader for both guest kinds — the <c>lxc</c> and
    /// <c>qemu</c> RRD endpoints differ only in the path segment and return the
    /// same sample shape.</summary>
    private async Task<IReadOnlyList<ProxmoxLxcRrdPoint>> GetGuestRrdDataAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, string timeframe, CancellationToken cancellationToken)
    {
        // Whitelist the timeframe — it's interpolated into the path and Proxmox
        // rejects anything outside this set anyway.
        var tf = timeframe is "hour" or "day" or "week" or "month" or "year" ? timeframe : "hour";
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/rrddata?timeframe={tf}&cf=AVERAGE", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxLxcRrdPoint>();
        foreach (var p in data.EnumerateArray())
        {
            var time = ReadLong(p, "time");
            if (time is null) continue;
            result.Add(new ProxmoxLxcRrdPoint(
                time.Value,
                ReadDouble(p, "cpu"),
                ReadLong(p, "mem"),
                ReadLong(p, "maxmem"),
                ReadLong(p, "netin"),
                ReadLong(p, "netout"),
                ReadLong(p, "diskread"),
                ReadLong(p, "diskwrite")));
        }
        return result;
    }

    public async Task<IReadOnlyList<ProxmoxTask>> GetLxcTasksAsync(
        ProxmoxConnectionProfile profile, int vmId, int limit, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/tasks?vmid={vmId}&limit={capped}&source=archive", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxTask>();
        foreach (var t in data.EnumerateArray())
        {
            var upid = t.TryGetProperty("upid", out var u) ? ReadString(u) : null;
            if (string.IsNullOrEmpty(upid)) continue;
            result.Add(new ProxmoxTask(
                upid,
                (t.TryGetProperty("type", out var ty) ? ReadString(ty) : null) ?? "task",
                t.TryGetProperty("status", out var st) ? ReadString(st) : null,
                t.TryGetProperty("user", out var us) ? ReadString(us) : null,
                ReadLong(t, "starttime"),
                ReadLong(t, "endtime")));
        }
        return result;
    }

    public async Task<string> GetTaskLogAsync(
        ProxmoxConnectionProfile profile, string upid, int limit, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 2000);
        var encoded = Uri.EscapeDataString(upid);
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/tasks/{encoded}/log?limit={capped}", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return string.Empty;

        // Each entry is { "n": <line no>, "t": "<text>" }; join in order.
        var lines = new List<string>();
        foreach (var line in data.EnumerateArray())
            if (line.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.String)
                lines.Add(tv.GetString() ?? string.Empty);
        return string.Join('\n', lines);
    }

    public Task<ProxmoxLxcStatus> GetLxcStatusAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default) =>
        GetGuestStatusAsync(profile, "lxc", vmId, cancellationToken);

    public Task<ProxmoxLxcStatus> GetQemuStatusAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default) =>
        GetGuestStatusAsync(profile, "qemu", vmId, cancellationToken);

    /// <summary>Shared live-status reader — the <c>lxc</c> and <c>qemu</c>
    /// <c>status/current</c> endpoints differ only in the path segment and return
    /// the same fields.</summary>
    private async Task<ProxmoxLxcStatus> GetGuestStatusAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/status/current", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return new ProxmoxLxcStatus("unknown", null, null, null, null, null, null, null, null, null, null);

        return new ProxmoxLxcStatus(
            (d.TryGetProperty("status", out var s) ? ReadString(s) : null) ?? "unknown",
            ReadDouble(d, "cpu"),
            ReadLong(d, "mem"),
            ReadLong(d, "maxmem"),
            ReadLong(d, "disk"),
            ReadLong(d, "maxdisk"),
            ReadLong(d, "netin"),
            ReadLong(d, "netout"),
            ReadLong(d, "diskread"),
            ReadLong(d, "diskwrite"),
            ReadLong(d, "uptime"));
    }

    public Task LxcStatusActionAsync(
        ProxmoxConnectionProfile profile, int vmId, string action, CancellationToken cancellationToken = default) =>
        GuestStatusActionAsync(profile, "lxc", vmId, action, cancellationToken);

    public Task QemuStatusActionAsync(
        ProxmoxConnectionProfile profile, int vmId, string action, CancellationToken cancellationToken = default) =>
        GuestStatusActionAsync(profile, "qemu", vmId, action, cancellationToken);

    /// <summary>Shared lifecycle POST for both guest kinds — same verb set, same
    /// <c>…/{kind}/{vmid}/status/{verb}</c> path shape.</summary>
    private async Task GuestStatusActionAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, string action, CancellationToken cancellationToken)
    {
        var verb = action is "start" or "stop" or "shutdown" or "reboot"
            ? action
            : throw new ArgumentException($"Unsupported {kind} action '{action}'.", nameof(action));
        await PostAsync(profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/status/{verb}", cancellationToken);
    }

    public async Task DeleteLxcAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        await DeleteAsync(profile, $"nodes/{profile.NodeName}/lxc/{vmId}", cancellationToken);
    }

    public async Task DeleteQemuAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        await DeleteAsync(profile, $"nodes/{profile.NodeName}/qemu/{vmId}", cancellationToken);
    }

    public async Task CreateLxcAsync(
        ProxmoxConnectionProfile profile, ProxmoxLxcCreate spec, CancellationToken cancellationToken = default)
    {
        var form = spec.Restore ? BuildRestoreForm(spec) : BuildCreateForm(spec);

        // The POST returns a task UPID; poll it to a terminal state so the caller
        // can report real success/failure (Proxmox provisions asynchronously).
        var upid = await PostFormReadUpidAsync(profile, $"nodes/{profile.NodeName}/lxc", form, cancellationToken);
        if (!string.IsNullOrEmpty(upid))
            await PollTaskAsync(profile, upid, cancellationToken);
    }

    /// <summary>Builds the create-from-template form. Proxmox merges these into a
    /// fresh container; only the fields the form collected are sent.</summary>
    private static List<KeyValuePair<string, string>> BuildCreateForm(ProxmoxLxcCreate spec)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("vmid", spec.VmId.ToString()),
            new("ostemplate", spec.OsTemplate),
            // The shorthand "storage:sizeInGiB" creates a new root volume on the
            // chosen storage at the requested size.
            new("rootfs", $"{spec.RootfsStorage}:{spec.RootfsSizeGib}"),
            new("unprivileged", spec.Unprivileged ? "1" : "0"),
            new("onboot", spec.Onboot ? "1" : "0"),
            new("start", spec.Start ? "1" : "0"),
        };
        if (!string.IsNullOrWhiteSpace(spec.Hostname)) form.Add(new("hostname", spec.Hostname.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Description)) form.Add(new("description", spec.Description.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Tags)) form.Add(new("tags", spec.Tags.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Password)) form.Add(new("password", spec.Password));
        if (!string.IsNullOrWhiteSpace(spec.SshPublicKeys)) form.Add(new("ssh-public-keys", spec.SshPublicKeys));
        if (spec.Cores is { } cores) form.Add(new("cores", cores.ToString()));
        if (spec.MemoryMib is { } mem) form.Add(new("memory", mem.ToString()));
        if (spec.SwapMib is { } swap) form.Add(new("swap", swap.ToString()));
        if (spec.Net0 is { } net) form.Add(new("net0", ProxmoxLxcConfigCodec.FormatNet(net)));
        if (spec.Nesting) form.Add(new("features", "nesting=1"));
        if (!string.IsNullOrWhiteSpace(spec.Nameserver)) form.Add(new("nameserver", spec.Nameserver.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.SearchDomain)) form.Add(new("searchdomain", spec.SearchDomain.Trim()));
        return form;
    }

    /// <summary>V8.1 — builds the restore-from-archive form. The archive volid rides
    /// in <c>ostemplate</c> with <c>restore=1</c>; the rootfs sizes and most config
    /// come from the backup, so we send <c>storage=</c> (the default-storage override,
    /// only when set) rather than a <c>rootfs</c> spec, and skip the template-only
    /// fields (password / SSH keys / net / DNS). <c>force=1</c> overwrites an existing
    /// vmid (the controller has already confirmed the target is stopped).</summary>
    private static List<KeyValuePair<string, string>> BuildRestoreForm(ProxmoxLxcCreate spec)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("vmid", spec.VmId.ToString()),
            new("ostemplate", spec.OsTemplate),
            new("restore", "1"),
            new("unprivileged", spec.Unprivileged ? "1" : "0"),
            new("onboot", spec.Onboot ? "1" : "0"),
            new("start", spec.Start ? "1" : "0"),
        };
        if (spec.Force) form.Add(new("force", "1"));
        // The default storage for volumes the archive doesn't pin elsewhere; blank ⇒
        // restore each volume onto the storage it was backed up from.
        if (!string.IsNullOrWhiteSpace(spec.RootfsStorage)) form.Add(new("storage", spec.RootfsStorage.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Hostname)) form.Add(new("hostname", spec.Hostname.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Description)) form.Add(new("description", spec.Description.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Tags)) form.Add(new("tags", spec.Tags.Trim()));
        if (spec.Cores is { } cores) form.Add(new("cores", cores.ToString()));
        if (spec.MemoryMib is { } mem) form.Add(new("memory", mem.ToString()));
        if (spec.SwapMib is { } swap) form.Add(new("swap", swap.ToString()));
        return form;
    }

    public async Task AddHaResourceAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        // sid "ct:<vmid>" implies the resource type; Proxmox 4xx's verbatim when HA
        // isn't configured, which PostFormReadUpidAsync surfaces as the host message.
        await PostFormReadUpidAsync(
            profile, "cluster/ha/resources", [new("sid", $"ct:{vmId}")], cancellationToken);
    }

    public async Task<int> GetNextVmIdAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync(profile, "cluster/nextid", cancellationToken);
        // /cluster/nextid returns { "data": "100" } — a string-encoded id.
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String && int.TryParse(data.GetString(), out var n)) return n;
            if (data.ValueKind == JsonValueKind.Number && data.TryGetInt32(out var m)) return m;
        }
        return 100;
    }

    public async Task<IReadOnlyList<ProxmoxTemplate>> ListTemplatesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // First find the storages that can hold container templates (their content
        // list advertises "vztmpl"), then read each one's vztmpl content.
        var storages = await GetNodeStoragesAsync(profile, cancellationToken);
        var templateStorages = storages
            .Where(s => s.Content?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => string.Equals(c, "vztmpl", StringComparison.OrdinalIgnoreCase)) ?? false)
            .Select(s => s.Storage);

        var result = new List<ProxmoxTemplate>();
        foreach (var storage in templateStorages)
        {
            using var doc = await GetJsonAsync(
                profile, $"nodes/{profile.NodeName}/storage/{Uri.EscapeDataString(storage)}/content?content=vztmpl",
                cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in data.EnumerateArray())
            {
                var volid = item.TryGetProperty("volid", out var v) ? ReadString(v) : null;
                if (string.IsNullOrEmpty(volid)) continue;
                result.Add(new ProxmoxTemplate(volid, storage, ReadLong(item, "size")));
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Volid, b.Volid));
        return result;
    }

    public async Task<IReadOnlyList<ProxmoxBackup>> ListBackupsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // First find the storages that can hold backups (their content list
        // advertises "backup"), then read each one's backup content. PBS datastores
        // need their own auth/namespace surface (out of scope), so we skip pbs:.
        var storages = await GetNodeStoragesAsync(profile, cancellationToken);
        var backupStorages = storages
            .Where(s => !string.Equals(s.Type, "pbs", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.Content?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => string.Equals(c, "backup", StringComparison.OrdinalIgnoreCase)) ?? false)
            .Select(s => s.Storage);

        var result = new List<ProxmoxBackup>();
        foreach (var storage in backupStorages)
        {
            using var doc = await GetJsonAsync(
                profile, $"nodes/{profile.NodeName}/storage/{Uri.EscapeDataString(storage)}/content?content=backup",
                cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in data.EnumerateArray())
            {
                var volid = item.TryGetProperty("volid", out var v) ? ReadString(v) : null;
                if (string.IsNullOrEmpty(volid)) continue;

                // Only LXC archives are restorable here — a VM backup (vzdump-qemu-*)
                // restores to a QEMU VM, which is a different endpoint. Proxmox tags
                // the row with subtype="lxc"; fall back to the volid filename for older
                // hosts that omit it.
                var subtype = item.TryGetProperty("subtype", out var st) ? ReadString(st) : null;
                var isLxc = string.Equals(subtype, "lxc", StringComparison.OrdinalIgnoreCase)
                    || (subtype is null && volid.Contains("vzdump-lxc-", StringComparison.OrdinalIgnoreCase));
                if (!isLxc) continue;

                result.Add(new ProxmoxBackup(
                    volid,
                    storage,
                    ReadLong(item, "vmid") is { } id ? (int)id : null,
                    ReadLong(item, "ctime"),
                    ReadLong(item, "size"),
                    item.TryGetProperty("format", out var f) ? ReadString(f) : null,
                    item.TryGetProperty("notes", out var n) ? ReadString(n) : null));
            }
        }
        // Newest first (a null ctime — rare — sorts last).
        result.Sort((a, b) => (b.CTime ?? long.MinValue).CompareTo(a.CTime ?? long.MinValue));
        return result;
    }

    public Task CloneLxcAsync(
        ProxmoxConnectionProfile profile, int sourceVmId, ProxmoxLxcClone spec, CancellationToken cancellationToken = default) =>
        CloneGuestAsync(profile, "lxc", sourceVmId, spec, cancellationToken);

    public Task CloneQemuAsync(
        ProxmoxConnectionProfile profile, int sourceVmId, ProxmoxLxcClone spec, CancellationToken cancellationToken = default) =>
        CloneGuestAsync(profile, "qemu", sourceVmId, spec, cancellationToken);

    /// <summary>Shared clone POST for both guest kinds — same form shape and async
    /// task UPID, differing only in the path segment and the new-name key
    /// (<c>hostname</c> for LXC, <c>name</c> for QEMU) plus the QEMU-only disk
    /// <c>format</c>.</summary>
    private async Task CloneGuestAsync(
        ProxmoxConnectionProfile profile, string kind, int sourceVmId, ProxmoxLxcClone spec, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("newid", spec.NewVmId.ToString()),
            new("full", spec.Full ? "1" : "0"),
        };
        // The new guest's name rides in a different key per kind.
        if (!string.IsNullOrWhiteSpace(spec.Hostname))
            form.Add(new(kind == "qemu" ? "name" : "hostname", spec.Hostname.Trim()));
        // The target storage only applies to a full clone (a linked clone references
        // the source's volumes), but Proxmox ignores it harmlessly for linked, so we
        // send it whenever set rather than second-guessing the host.
        if (!string.IsNullOrWhiteSpace(spec.TargetStorage)) form.Add(new("storage", spec.TargetStorage.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.SnapName)) form.Add(new("snapname", spec.SnapName.Trim()));
        if (!string.IsNullOrWhiteSpace(spec.Description)) form.Add(new("description", spec.Description.Trim()));
        // The disk format is a QEMU-only full-clone option; the LXC endpoint has no
        // such key, so only emit it for a VM.
        if (kind == "qemu" && !string.IsNullOrWhiteSpace(spec.Format)) form.Add(new("format", spec.Format.Trim()));

        var upid = await PostFormReadUpidAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{sourceVmId}/clone", form, cancellationToken);
        if (!string.IsNullOrEmpty(upid))
            await PollTaskAsync(profile, upid, cancellationToken);
    }

    public Task<IReadOnlyList<ProxmoxSnapshot>> ListLxcSnapshotsAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default) =>
        ListGuestSnapshotsAsync(profile, "lxc", vmId, cancellationToken);

    public Task<IReadOnlyList<ProxmoxSnapshot>> ListQemuSnapshotsAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default) =>
        ListGuestSnapshotsAsync(profile, "qemu", vmId, cancellationToken);

    /// <summary>Shared snapshot list for both guest kinds — the <c>lxc</c> and
    /// <c>qemu</c> snapshot endpoints differ only in the path segment and return the
    /// same fields (a QEMU snapshot may additionally carry <c>vmstate</c>).</summary>
    private async Task<IReadOnlyList<ProxmoxSnapshot>> ListGuestSnapshotsAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/snapshot", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxSnapshot>();
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? ReadString(n) : null;
            // The synthetic "current" pseudo-entry is the live state, not a real
            // snapshot — drop it so the list is purely restorable points.
            if (string.IsNullOrEmpty(name) || string.Equals(name, "current", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new ProxmoxSnapshot(
                name,
                item.TryGetProperty("description", out var d) ? ReadString(d) : null,
                ReadLong(item, "snaptime"),
                item.TryGetProperty("parent", out var p) ? ReadString(p) : null,
                ReadBool(item, "vmstate") ?? false));
        }
        // Newest first (a null snaptime — rare — sorts last).
        result.Sort((a, b) => (b.SnapTime ?? long.MinValue).CompareTo(a.SnapTime ?? long.MinValue));
        return result;
    }

    public Task CreateLxcSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, string? description,
        CancellationToken cancellationToken = default) =>
        // An LXC snapshot has no running-memory option (its schema rejects vmstate),
        // so the shared helper is always called with vmstate off for the LXC kind.
        CreateGuestSnapshotAsync(profile, "lxc", vmId, name, description, vmstate: false, cancellationToken);

    public Task CreateQemuSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, string? description, bool vmstate,
        CancellationToken cancellationToken = default) =>
        CreateGuestSnapshotAsync(profile, "qemu", vmId, name, description, vmstate, cancellationToken);

    /// <summary>Shared snapshot-create POST for both guest kinds. <c>vmstate</c> is
    /// QEMU-only (the LXC endpoint rejects it), so it is only emitted for a VM and
    /// only when requested.</summary>
    private async Task CreateGuestSnapshotAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, string name, string? description, bool vmstate,
        CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("snapname", name) };
        if (!string.IsNullOrWhiteSpace(description)) form.Add(new("description", description.Trim()));
        if (kind == "qemu" && vmstate) form.Add(new("vmstate", "1"));

        var upid = await PostFormReadUpidAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/snapshot", form, cancellationToken);
        if (!string.IsNullOrEmpty(upid))
            await PollTaskAsync(profile, upid, cancellationToken);
    }

    public Task RollbackLxcSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default) =>
        RollbackGuestSnapshotAsync(profile, "lxc", vmId, name, cancellationToken);

    public Task RollbackQemuSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default) =>
        RollbackGuestSnapshotAsync(profile, "qemu", vmId, name, cancellationToken);

    /// <summary>Shared rollback POST for both guest kinds — a pure path-segment
    /// swap.</summary>
    private async Task RollbackGuestSnapshotAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, string name, CancellationToken cancellationToken)
    {
        var upid = await PostFormReadUpidAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/snapshot/{Uri.EscapeDataString(name)}/rollback",
            [], cancellationToken);
        if (!string.IsNullOrEmpty(upid))
            await PollTaskAsync(profile, upid, cancellationToken);
    }

    public Task DeleteLxcSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default) =>
        DeleteGuestSnapshotAsync(profile, "lxc", vmId, name, cancellationToken);

    public Task DeleteQemuSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default) =>
        DeleteGuestSnapshotAsync(profile, "qemu", vmId, name, cancellationToken);

    /// <summary>Shared snapshot-delete for both guest kinds — a path-segment swap.
    /// Snapshot delete is asynchronous on Proxmox (the DELETE returns a task UPID),
    /// so poll it to a terminal state like the other write actions.</summary>
    private async Task DeleteGuestSnapshotAsync(
        ProxmoxConnectionProfile profile, string kind, int vmId, string name, CancellationToken cancellationToken)
    {
        var upid = await DeleteReadUpidAsync(
            profile, $"nodes/{profile.NodeName}/{kind}/{vmId}/snapshot/{Uri.EscapeDataString(name)}", cancellationToken);
        if (!string.IsNullOrEmpty(upid))
            await PollTaskAsync(profile, upid, cancellationToken);
    }

    /// <summary>Reads one task's status (<c>GET …/tasks/{upid}/status</c>):
    /// <c>(running, exitStatus)</c>. A finished task reports <c>status ==
    /// "stopped"</c> with an <c>exitstatus</c> string (<c>OK</c> on success).</summary>
    private async Task<(bool Running, string? ExitStatus)> GetTaskStatusAsync(
        ProxmoxConnectionProfile profile, string upid, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/tasks/{Uri.EscapeDataString(upid)}/status", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return (false, null);
        var status = d.TryGetProperty("status", out var s) ? ReadString(s) : null;
        var exit = d.TryGetProperty("exitstatus", out var e) ? ReadString(e) : null;
        return (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase), exit);
    }

    /// <summary>Polls a task to a terminal state, then throws when its exit status
    /// isn't <c>OK</c> so the caller surfaces the host's own failure reason. Bounded
    /// so a stuck task can't hang the request forever.</summary>
    private async Task PollTaskAsync(ProxmoxConnectionProfile profile, string upid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            var (running, exit) = await GetTaskStatusAsync(profile, upid, cancellationToken);
            if (!running)
            {
                if (exit is not null && !string.Equals(exit, "OK", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException($"Proxmox task failed: {exit}");
                return;
            }
            if (DateTime.UtcNow >= deadline)
                throw new HttpRequestException("Proxmox create task did not finish in time (still running after 5 minutes).");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    public async Task UpdateLxcConfigAsync(
        ProxmoxConnectionProfile profile, int vmId, ProxmoxLxcConfigUpdate update, CancellationToken cancellationToken = default)
    {
        // Only send the fields the caller actually set — Proxmox merges the
        // form keys into the existing config, so omitting a key leaves it as-is.
        var form = new List<KeyValuePair<string, string>>();
        if (update.Cores is { } cores) form.Add(new("cores", cores.ToString()));
        if (update.MemoryMib is { } mem) form.Add(new("memory", mem.ToString()));
        if (update.SwapMib is { } swap) form.Add(new("swap", swap.ToString()));
        if (update.Hostname is { } host) form.Add(new("hostname", host));
        if (update.Onboot is { } onboot) form.Add(new("onboot", onboot ? "1" : "0"));

        // ── V6.9 — structured network / mount / rootfs edits ──────────────────
        var deletes = new List<string>();

        // Adds (a change with an empty key) need the next free net<n> / mp<n>.
        // Read the current config once, only when there's actually an add, so the
        // server — not the client — owns key numbering.
        var needsKeys =
            (update.Networks?.Any(n => !n.Remove && string.IsNullOrWhiteSpace(n.Key)) ?? false)
            || (update.Mounts?.Any(m => !m.Remove && string.IsNullOrWhiteSpace(m.Key)) ?? false);
        var netKeys = new KeyAllocator("net");
        var mpKeys = new KeyAllocator("mp");
        if (needsKeys)
        {
            var detail = await GetLxcDetailAsync(profile, vmId, cancellationToken);
            netKeys.Seed(detail.Networks.Select(n => n.Key));
            mpKeys.Seed(detail.Mounts.Select(m => m.Key));
        }

        if (update.Networks is { } nets)
        {
            foreach (var n in nets)
            {
                if (n.Remove) { deletes.Add(n.Key); continue; }
                var key = string.IsNullOrWhiteSpace(n.Key) ? netKeys.Next() : n.Key.Trim();
                form.Add(new(key, ProxmoxLxcConfigCodec.FormatNet(n)));
            }
        }

        if (update.Mounts is { } mounts)
        {
            foreach (var m in mounts)
            {
                if (m.Remove) { deletes.Add(m.Key); continue; }
                var key = string.IsNullOrWhiteSpace(m.Key) ? mpKeys.Next() : m.Key.Trim();
                form.Add(new(key, ProxmoxLxcConfigCodec.FormatMount(m)));
            }
        }

        if (update.Rootfs is { } rootfs)
            form.Add(new("rootfs", ProxmoxLxcConfigCodec.FormatRootfs(rootfs)));

        // Removals flow through Proxmox's own delete= semantics, never ad-hoc
        // string rewriting — one comma-separated list of the keys to drop.
        if (deletes.Count > 0)
            form.Add(new("delete", string.Join(',', deletes)));

        if (form.Count == 0) return;   // nothing changed — don't bother the host
        await PutFormAsync(profile, $"nodes/{profile.NodeName}/lxc/{vmId}/config", form, cancellationToken);
    }

    /// <summary>Hands out the lowest free <c>{prefix}{n}</c> key, seeded from the
    /// keys already on the container so a new interface / mount never collides
    /// with an existing one (or with another add in the same batch).</summary>
    private sealed class KeyAllocator(string prefix)
    {
        private readonly HashSet<int> _used = [];

        public void Seed(IEnumerable<string> keys)
        {
            foreach (var k in keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(k.AsSpan(prefix.Length), out var n))
                    _used.Add(n);
        }

        public string Next()
        {
            var i = 0;
            while (!_used.Add(i)) i++;
            return $"{prefix}{i}";
        }
    }

    public async Task<int> GetNodePendingUpdatesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // /nodes/{node}/apt/update returns one entry per upgradable package, so
        // the array length is the pending-update count.
        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/apt/update", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0;
        return data.GetArrayLength();
    }

    // ── V6.8 — PVE node card ─────────────────────────────────────────────────

    public async Task<ProxmoxNodeStatus> GetNodeStatusAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        string? cpuModel = null, kernel = null, pveVersion = null;
        int? sockets = null, cpus = null, cores = null;
        double? cpuMhz = null, cpu = null, wait = null, load1 = null, load5 = null, load15 = null;
        bool? hvm = null;
        long? memTotal = null, memUsed = null, memFree = null, swapTotal = null, swapUsed = null;
        long? rootTotal = null, rootUsed = null, uptime = null;

        using (var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/status", cancellationToken))
        {
            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                cpu = ReadDouble(d, "cpu");
                wait = ReadDouble(d, "wait");
                uptime = ReadLong(d, "uptime");
                kernel = d.TryGetProperty("kversion", out var kv) ? ReadString(kv) : null;
                pveVersion = d.TryGetProperty("pveversion", out var pv) ? ReadString(pv) : null;

                if (d.TryGetProperty("loadavg", out var la) && la.ValueKind == JsonValueKind.Array)
                {
                    var arr = la.EnumerateArray().ToArray();
                    if (arr.Length > 0) load1 = ReadDoubleValue(arr[0]);
                    if (arr.Length > 1) load5 = ReadDoubleValue(arr[1]);
                    if (arr.Length > 2) load15 = ReadDoubleValue(arr[2]);
                }
                if (d.TryGetProperty("cpuinfo", out var ci) && ci.ValueKind == JsonValueKind.Object)
                {
                    cpuModel = ci.TryGetProperty("model", out var cm) ? ReadString(cm) : null;
                    sockets = ReadInt(ci, "sockets");
                    cpus = ReadInt(ci, "cpus");
                    cores = ReadInt(ci, "cores");
                    cpuMhz = ReadDouble(ci, "mhz");
                    hvm = ReadBool(ci, "hvm");
                }
                if (d.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.Object)
                {
                    memTotal = ReadLong(mem, "total");
                    memUsed = ReadLong(mem, "used");
                    memFree = ReadLong(mem, "free");
                }
                if (d.TryGetProperty("swap", out var sw) && sw.ValueKind == JsonValueKind.Object)
                {
                    swapTotal = ReadLong(sw, "total");
                    swapUsed = ReadLong(sw, "used");
                }
                // PVE reports the host filesystem under "rootfs"; PBS under "root".
                if ((d.TryGetProperty("rootfs", out var rf) || d.TryGetProperty("root", out rf))
                    && rf.ValueKind == JsonValueKind.Object)
                {
                    rootTotal = ReadLong(rf, "total");
                    rootUsed = ReadLong(rf, "used");
                }
                // PBS nests the kernel version under "info" rather than the
                // top-level "kversion" PVE uses.
                if (kernel is null && d.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                    kernel = info.TryGetProperty("kversion", out var ikv) ? ReadString(ikv) : null;
            }
        }

        // PBS has no "pveversion" in node status — read its product version from
        // the shared /version endpoint instead (best-effort).
        if (pveVersion is null && profile.ServerType == ProxmoxServerType.Pbs)
        {
            try
            {
                using var ver = await GetJsonAsync(profile, "version", cancellationToken);
                if (ver.RootElement.TryGetProperty("data", out var vd) && vd.ValueKind == JsonValueKind.Object)
                {
                    var v = vd.TryGetProperty("version", out var vv) ? ReadString(vv) : null;
                    if (!string.IsNullOrEmpty(v)) pveVersion = $"PBS {v}";
                }
            }
            catch
            {
                // Best-effort — leave version null.
            }
        }

        // Subscription is enrichment — a host without a subscription returns a
        // benign "notfound", and the call must never fail the whole snapshot.
        string? subStatus = null, subLevel = null;
        try
        {
            using var sub = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/subscription", cancellationToken);
            if (sub.RootElement.TryGetProperty("data", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                subStatus = sd.TryGetProperty("status", out var ss) ? ReadString(ss) : null;
                subLevel = sd.TryGetProperty("level", out var sl) ? ReadString(sl) : null;
            }
        }
        catch
        {
            // Best-effort — leave subscription null.
        }

        return new ProxmoxNodeStatus(
            cpuModel, sockets, cpus, cores, cpuMhz, hvm, cpu, wait, load1, load5, load15,
            memTotal, memUsed, memFree, swapTotal, swapUsed, rootTotal, rootUsed,
            uptime, kernel, pveVersion, subStatus, subLevel);
    }

    public async Task<IReadOnlyList<ProxmoxNodeRrdPoint>> GetNodeRrdDataAsync(
        ProxmoxConnectionProfile profile, string timeframe, CancellationToken cancellationToken = default)
    {
        var tf = timeframe is "hour" or "day" or "week" or "month" or "year" ? timeframe : "hour";
        using var doc = await GetJsonAsync(
            profile, $"nodes/{profile.NodeName}/rrddata?timeframe={tf}&cf=AVERAGE", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxNodeRrdPoint>();
        foreach (var p in data.EnumerateArray())
        {
            var time = ReadLong(p, "time");
            if (time is null) continue;
            result.Add(new ProxmoxNodeRrdPoint(
                time.Value,
                ReadDouble(p, "cpu"),
                ReadDouble(p, "iowait"),
                ReadDouble(p, "loadavg"),
                ReadLong(p, "memtotal"),
                ReadLong(p, "memused"),
                ReadLong(p, "netin"),
                ReadLong(p, "netout"),
                ReadLong(p, "roottotal"),
                ReadLong(p, "rootused"),
                ReadLong(p, "swapused")));
        }
        return result;
    }

    public async Task<IReadOnlyList<ProxmoxNodeStorage>> GetNodeStoragesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        // PBS has no PVE-style storage pools — its analogue is the set of
        // datastores, whose usage comes from /status/datastore-usage. Mapped onto
        // the same ProxmoxNodeStorage shape so the Storage tab + the storage
        // alert category work unchanged.
        if (profile.ServerType == ProxmoxServerType.Pbs)
            return await GetPbsDatastoresAsync(profile, cancellationToken);

        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/storage", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxNodeStorage>();
        foreach (var s in data.EnumerateArray())
        {
            var name = s.TryGetProperty("storage", out var sn) ? ReadString(sn) : null;
            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new ProxmoxNodeStorage(
                name,
                s.TryGetProperty("type", out var ty) ? ReadString(ty) : null,
                s.TryGetProperty("content", out var co) ? ReadString(co) : null,
                ReadBool(s, "enabled") ?? true,
                ReadBool(s, "active") ?? false,
                ReadLong(s, "total"),
                ReadLong(s, "used"),
                ReadLong(s, "avail")));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Storage, b.Storage));
        return result;
    }

    /// <summary>V6.8.3 — PBS datastores as the storage-pool analogue, from
    /// <c>GET /status/datastore-usage</c> (one row per datastore with byte
    /// totals). A datastore is always "active"; usage drives the same meters +
    /// storage-fullness alert as a PVE pool.</summary>
    private async Task<IReadOnlyList<ProxmoxNodeStorage>> GetPbsDatastoresAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(profile, "status/datastore-usage", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxNodeStorage>();
        foreach (var s in data.EnumerateArray())
        {
            var name = s.TryGetProperty("store", out var sn) ? ReadString(sn) : null;
            if (string.IsNullOrEmpty(name)) continue;
            var total = ReadLong(s, "total");
            var used = ReadLong(s, "used");
            var avail = ReadLong(s, "avail") ?? (total is { } t && used is { } u ? t - u : null);
            result.Add(new ProxmoxNodeStorage(
                name, "datastore", "backup", Enabled: true, Active: true, total, used, avail));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Storage, b.Storage));
        return result;
    }

    public async Task<IReadOnlyList<ProxmoxNodeDisk>> GetNodeDisksAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/disks/list", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxNodeDisk>();
        foreach (var d in data.EnumerateArray())
        {
            var devPath = d.TryGetProperty("devpath", out var dp) ? ReadString(dp) : null;
            if (string.IsNullOrEmpty(devPath)) continue;
            result.Add(new ProxmoxNodeDisk(
                devPath,
                d.TryGetProperty("model", out var md) ? ReadString(md) : null,
                d.TryGetProperty("serial", out var sr) ? ReadString(sr) : null,
                d.TryGetProperty("vendor", out var vd) ? ReadString(vd) : null,
                // V7.2.1 — PVE names these "type" / "health"; PBS names the same
                // columns "disk-type" / "status" (and reports e.g. "hdd" / "passed").
                // Read the PVE key first, fall back to the PBS key, so the type chip
                // and the SMART-health badge populate on PBS instead of showing a
                // blank type + an "UNKNOWN" health.
                ReadFirstString(d, "type", "disk-type"),
                ReadLong(d, "size"),
                ReadFirstString(d, "health", "status"),
                ReadInt(d, "wearout"),   // SSD media-life-remaining %, or absent
                ReadInt(d, "rpm"),
                d.TryGetProperty("used", out var us) ? ReadString(us) : null));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.DevPath, b.DevPath));
        return result;
    }

    public async Task<ProxmoxNodeDiskSmart> GetNodeDiskSmartAsync(
        ProxmoxConnectionProfile profile, string devPath, CancellationToken cancellationToken = default)
    {
        // V7.2.1 — PBS validates the `disk` parameter against its block-device
        // name schema: a bare name (`sda` / `nvme0n1`, as found under
        // /sys/block/<name>), NOT the `/dev/`-prefixed path PVE accepts. Sending
        // `/dev/sda` to PBS fails its regex with a 400 ("'disk': value does not
        // match the regex pattern"), so strip the prefix for PBS; PVE keeps the
        // full devpath it has always used.
        var diskParam = profile.ServerType == ProxmoxServerType.Pbs && devPath.StartsWith("/dev/", StringComparison.Ordinal)
            ? devPath["/dev/".Length..]
            : devPath;
        var disk = Uri.EscapeDataString(diskParam);
        var (doc, hostError) = await GetJsonOrHostErrorAsync(
            profile, $"nodes/{profile.NodeName}/disks/smart?disk={disk}", cancellationToken);

        // A per-disk SMART read can still fail for THIS disk while the host is
        // perfectly reachable — the host runs `smartctl`, which exits non-zero for
        // a USB-bridged disk, a disk that can't report SMART, etc., and Proxmox
        // relays that as a 4xx. That is a disk-level condition, not a
        // host-reachability failure, so surface the host's own reason as the SMART
        // text (it renders inline under the disk) instead of letting it bubble up
        // as a scary "host unreachable" 502. A genuine transport failure (host
        // actually down) still throws below.
        if (doc is null)
            return new ProxmoxNodeDiskSmart(null, null, [], $"SMART unavailable for {devPath}: {hostError}");

        using var _ = doc;
        if (!doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return new ProxmoxNodeDiskSmart(null, null, [], null);

        var health = d.TryGetProperty("health", out var hl) ? ReadString(hl) : null;
        var type = d.TryGetProperty("type", out var ty) ? ReadString(ty) : null;
        var text = d.TryGetProperty("text", out var tx) ? ReadString(tx) : null;

        var attrs = new List<ProxmoxSmartAttribute>();
        if (d.TryGetProperty("attributes", out var av) && av.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in av.EnumerateArray())
            {
                var id = ReadInt(a, "id");
                var name = a.TryGetProperty("name", out var an) ? ReadString(an) : null;
                if (id is null || string.IsNullOrEmpty(name)) continue;
                attrs.Add(new ProxmoxSmartAttribute(
                    id.Value,
                    name,
                    ReadLong(a, "value"),
                    ReadLong(a, "worst"),
                    ReadLong(a, "threshold"),
                    a.TryGetProperty("raw", out var rw) ? ReadString(rw) : null));
            }
        }
        return new ProxmoxNodeDiskSmart(health, type, attrs, text);
    }

    public async Task<IReadOnlyList<ProxmoxNodeNetworkInterface>> GetNodeNetworkInterfacesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync(profile, $"nodes/{profile.NodeName}/network", cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ProxmoxNodeNetworkInterface>();
        foreach (var n in data.EnumerateArray())
        {
            var iface = n.TryGetProperty("iface", out var nm) ? ReadString(nm) : null;
            if (string.IsNullOrEmpty(iface)) continue;
            result.Add(new ProxmoxNodeNetworkInterface(
                iface,
                n.TryGetProperty("type", out var ty) ? ReadString(ty) : null,
                ReadBool(n, "active"),
                ReadBool(n, "autostart"),
                n.TryGetProperty("method", out var me) ? ReadString(me) : null,
                n.TryGetProperty("address", out var ad) ? ReadString(ad) : null,
                n.TryGetProperty("cidr", out var ci) ? ReadString(ci) : null,
                n.TryGetProperty("gateway", out var gw) ? ReadString(gw) : null,
                n.TryGetProperty("bridge_ports", out var bp) ? ReadString(bp) : null,
                n.TryGetProperty("bond_slaves", out var bs) ? ReadString(bs) : null));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Iface, b.Iface));
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(
        ProxmoxConnectionProfile profile, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/{path}");
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>V7.2.1 — like <see cref="GetJsonAsync"/> but for an endpoint where
    /// a non-success status is a per-resource condition rather than a host
    /// reachability failure (e.g. <c>disks/smart</c>, where <c>smartctl</c> can
    /// fail for one disk while the host is fine). Returns the parsed document on
    /// success, or <c>(null, hostMessage)</c> carrying the host's own error body
    /// on a 4xx/5xx — it never throws on a non-success <em>response</em>. A
    /// genuine transport failure (connection refused / no route to host) still
    /// throws, so the caller can keep treating that as "host unreachable".</summary>
    private async Task<(JsonDocument? Doc, string? Error)> GetJsonOrHostErrorAsync(
        ProxmoxConnectionProfile profile, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/{path}");
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            return (null, $"Proxmox returned {(int)response.StatusCode}: {detail}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken), null);
    }

    /// <summary>POSTs to a Proxmox path (e.g. a lifecycle action). The
    /// <c>PVEAPIToken</c> scheme needs no CSRF token, so an empty body is fine.</summary>
    private async Task PostAsync(
        ProxmoxConnectionProfile profile, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api2/json/{path}");
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>DELETEs a Proxmox resource (e.g. an LXC). Like
    /// <see cref="PutFormAsync"/>, on a non-success status it reads the Proxmox
    /// error body and throws an <see cref="HttpRequestException"/> carrying it, so
    /// the controller can surface the host's real message (e.g. a refusal to
    /// destroy a running guest, or a permission error) instead of a bare
    /// status code.</summary>
    private async Task DeleteAsync(
        ProxmoxConnectionProfile profile, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api2/json/{path}");
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Proxmox returned {(int)response.StatusCode}: {detail}");
        }
    }

    /// <summary>V8.0 — DELETEs a Proxmox resource that completes asynchronously
    /// (e.g. a snapshot) and returns the task UPID from the response's <c>data</c>
    /// field so the caller can poll it. Like <see cref="DeleteAsync"/>, a non-success
    /// status reads the host error body and throws an
    /// <see cref="HttpRequestException"/> carrying it.</summary>
    private async Task<string?> DeleteReadUpidAsync(
        ProxmoxConnectionProfile profile, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api2/json/{path}");
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Proxmox returned {(int)response.StatusCode}: {detail}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("data", out var data) ? ReadString(data) : null;
    }

    /// <summary>PUTs a form-url-encoded body (the Proxmox config-write shape).
    /// On a non-success status it reads the Proxmox error body and throws an
    /// <see cref="HttpRequestException"/> carrying it, so the controller can
    /// surface a real message (e.g. a 403 from a token missing
    /// <c>VM.Config.*</c>) instead of a bare status code.</summary>
    private async Task PutFormAsync(
        ProxmoxConnectionProfile profile,
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api2/json/{path}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Proxmox returned {(int)response.StatusCode}: {detail}");
        }
    }

    /// <summary>POSTs a form-url-encoded body (the Proxmox create shape) and
    /// returns the task UPID from the response's <c>data</c> field. On a non-success
    /// status it reads the Proxmox error body and throws an
    /// <see cref="HttpRequestException"/> carrying it (e.g. a 400 for a vmid already
    /// in use, or a storage that can't hold a rootfs), so the controller can relay
    /// the host's real message.</summary>
    private async Task<string?> PostFormReadUpidAsync(
        ProxmoxConnectionProfile profile,
        string path,
        IReadOnlyList<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? InsecureHttpClientName : HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api2/json/{path}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Proxmox returned {(int)response.StatusCode}: {detail}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("data", out var data) ? ReadString(data) : null;
    }

    /// <summary>Reads an integer property that Proxmox may encode as a JSON
    /// number or a string.</summary>
    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.Number when value.TryGetDouble(out var d) => (int)d,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>Reads a 64-bit integer property (byte counts, uptime) that
    /// Proxmox may encode as a JSON number or string.</summary>
    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.Number when value.TryGetDouble(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>Reads a fractional property (e.g. <c>cpu</c> as 0..1).</summary>
    private static double? ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            // Proxmox encodes some fractionals as dot-decimal strings (e.g.
            // cpuinfo.mhz "3100.00"); parse invariant so a non-US server locale
            // doesn't read them as null. Mirrors ReadDoubleValue.
            JsonValueKind.String when double.TryParse(
                value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    /// <summary>Reads a fractional value straight from an element (e.g. a
    /// <c>loadavg</c> array entry, which Proxmox encodes as a string).</summary>
    private static double? ReadDoubleValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };

    /// <summary>Reads a string property; returns <c>null</c> for non-strings.</summary>
    private static string? ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>V7.2.1 — reads the first present string property among
    /// <paramref name="names"/>, for fields PVE and PBS expose under different
    /// keys (e.g. <c>type</c>/<c>disk-type</c>, <c>health</c>/<c>status</c>).</summary>
    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    /// <summary>Reads a Proxmox boolean flag, encoded as <c>1</c> / <c>0</c>
    /// (number or string).</summary>
    private static bool? ReadBool(JsonElement element, string property)
    {
        var n = ReadInt(element, property);
        return n is null ? null : n != 0;
    }

    /// <summary>MiB → bytes (Proxmox config reports memory / swap in MiB).</summary>
    private static long? MebibytesToBytes(long? mib) => mib is null ? null : mib.Value * 1024 * 1024;

    /// <summary>True for a key like <c>net0</c> / <c>mp3</c> — the prefix
    /// followed by one or more digits.</summary>
    private static bool IsIndexed(string key, string prefix) =>
        key.Length > prefix.Length
        && key.StartsWith(prefix, StringComparison.Ordinal)
        && key.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') == -1;

    /// <summary>V6.14 — true for a QEMU disk key (<c>scsi0</c>, <c>virtio1</c>,
    /// <c>ide2</c>, <c>sata0</c>, <c>efidisk0</c>, <c>tpmstate0</c>) — a known disk
    /// prefix followed by digits.</summary>
    private static bool IsDiskKey(string key) =>
        IsIndexed(key, "scsi") || IsIndexed(key, "virtio") || IsIndexed(key, "ide")
        || IsIndexed(key, "sata") || IsIndexed(key, "efidisk") || IsIndexed(key, "tpmstate");

    /// <summary>Appends a raw config line (e.g. <c>net0</c>) when its value is a
    /// non-empty string.</summary>
    private static void AddLine(List<ProxmoxLxcConfigLine> target, JsonProperty prop)
    {
        var raw = ReadString(prop.Value);
        if (!string.IsNullOrWhiteSpace(raw)) target.Add(new ProxmoxLxcConfigLine(prop.Name, raw));
    }
}
