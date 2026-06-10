using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V6.0 — production <see cref="IProxmoxGuestInspector"/>. SSHes to the Proxmox
/// host and runs <c>pct exec &lt;vmid&gt; -- …</c> to count pending updates
/// inside a Debian-based LXC. A fresh, short-lived SSH session is opened per
/// call: scans run hourly/daily at most, and the per-LXC interface boundary
/// keeps the orchestrator mockable — so the extra handshakes are an acceptable
/// trade for simplicity at homelab scale.
/// </summary>
internal sealed class ProxmoxSshGuestInspector(ILogger<ProxmoxSshGuestInspector> logger) : IProxmoxGuestInspector
{
    private const int ConnectTimeoutSeconds = 15;
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Counts upgradable packages inside the LXC. The remote one-liner guards
    /// for non-Debian guests (no <c>apt</c>) by emitting the sentinel
    /// <c>NOAPT</c>, and forces a <c>0</c> (rather than grep's exit-1) when the
    /// container is fully up to date — so a clean Debian box and a non-Debian
    /// one are never confused.
    /// </summary>
    public Task<ProxmoxGuestInspection> CountPendingUpdatesAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxGuestInspection(null, "SSH is not configured for this Proxmox host."));

        const string script =
            "if command -v apt >/dev/null 2>&1; then " +
            "apt list --upgradable 2>/dev/null | grep -c upgradable || true; " +
            "else echo NOAPT; fi";
        var command = $"pct exec {vmId} -- sh -c '{script}'";

        try
        {
            var (exitStatus, stdout, stderr) = RunCommand(profile.Ssh, command, cancellationToken);
            var output = stdout.Trim();

            if (output == "NOAPT")
                return Task.FromResult(new ProxmoxGuestInspection(null, "Not a Debian/apt-based container."));

            if (int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return Task.FromResult(new ProxmoxGuestInspection(count, null));

            // Non-numeric output with a non-zero exit usually means `pct exec`
            // itself failed (guest not running, vmid gone). Surface the host's
            // stderr so the card explains why the count is missing.
            var detail = string.IsNullOrWhiteSpace(stderr) ? output : stderr.Trim();
            var message = string.IsNullOrWhiteSpace(detail)
                ? $"Could not read update count (pct exec exited {exitStatus})."
                : $"Could not read update count: {detail}";
            return Task.FromResult(new ProxmoxGuestInspection(null, message));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox pct exec failed for vmid {VmId}", vmId);
            return Task.FromResult(new ProxmoxGuestInspection(null, ex.Message));
        }
    }

    public Task<bool> TestConnectionAsync(ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null) return Task.FromResult(false);
        try
        {
            var (exitStatus, _, _) = RunCommand(profile.Ssh, "echo stashboard-ok", cancellationToken);
            return Task.FromResult(exitStatus == 0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox SSH test failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// V6.8 — reads CPU/board temperatures and fan RPMs by running
    /// <c>sensors -j</c> over SSH. These signals are not in the Proxmox REST API.
    /// The remote one-liner emits the sentinel <c>NOSENSORS</c> when
    /// <c>lm-sensors</c> isn't installed, so a host without it is reported as
    /// "not available" rather than an error.
    /// </summary>
    public Task<ProxmoxNodeSensors> ReadNodeSensorsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeSensors(false, "SSH is not configured for this Proxmox host.", []));

        const string command =
            "if command -v sensors >/dev/null 2>&1; then sensors -j 2>/dev/null; else echo NOSENSORS; fi";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var output = stdout.Trim();
            if (output is "NOSENSORS" or "")
                return Task.FromResult(new ProxmoxNodeSensors(
                    false, "lm-sensors is not installed on this host (apt install lm-sensors).", []));

            var readings = ParseSensors(output);
            return Task.FromResult(readings.Count == 0
                ? new ProxmoxNodeSensors(false, "No temperature or fan sensors were detected on this host.", [])
                : new ProxmoxNodeSensors(true, null, readings));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox sensors read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeSensors(false, ex.Message, []));
        }
    }

    /// <summary>Parses <c>sensors -j</c> JSON into flat readings. The shape is
    /// <c>{ chip: { "Adapter": "...", label: { tempN_input: x, tempN_max: y,
    /// tempN_crit: z }, label2: { fanN_input: r } } }</c>. We walk every feature
    /// object and turn each <c>*_input</c> key into a temperature, fan, voltage
    /// (V6.8.2 — <c>in*</c>), or power (V6.8.2 — <c>power*</c>) reading, pulling
    /// the matching <c>*_max</c> / <c>*_crit</c> thresholds for temperatures.
    /// Never throws past the caller's catch — a malformed chip is skipped.</summary>
    internal static IReadOnlyList<ProxmoxSensorReading> ParseSensors(string json)
    {
        var readings = new List<ProxmoxSensorReading>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return readings;

        foreach (var chip in doc.RootElement.EnumerateObject())
        {
            if (chip.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var feature in chip.Value.EnumerateObject())
            {
                if (feature.Value.ValueKind != JsonValueKind.Object) continue;  // skip "Adapter"
                foreach (var metric in feature.Value.EnumerateObject())
                {
                    if (!metric.Name.EndsWith("_input", StringComparison.Ordinal)) continue;
                    if (metric.Value.ValueKind != JsonValueKind.Number) continue;
                    var value = metric.Value.GetDouble();
                    var basePrefix = metric.Name[..^"_input".Length];   // e.g. "temp1" / "fan1" / "in0" / "power1"

                    if (basePrefix.StartsWith("temp", StringComparison.Ordinal))
                    {
                        readings.Add(new ProxmoxSensorReading(
                            chip.Name, feature.Name, value,
                            ReadFeatureDouble(feature.Value, $"{basePrefix}_max"),
                            ReadFeatureDouble(feature.Value, $"{basePrefix}_crit"),
                            null));
                    }
                    else if (basePrefix.StartsWith("fan", StringComparison.Ordinal))
                    {
                        readings.Add(new ProxmoxSensorReading(
                            chip.Name, feature.Name, null, null, null, value));
                    }
                    // Voltage rails are exposed as `in<n>_input` (volts); PSU/CPU
                    // power as `power<n>_input` (watts). Both are flat scalar
                    // inputs with no high/crit pair we render.
                    else if (basePrefix.StartsWith("in", StringComparison.Ordinal)
                             && basePrefix.Length > 2 && char.IsDigit(basePrefix[2]))
                    {
                        readings.Add(new ProxmoxSensorReading(
                            chip.Name, feature.Name, null, null, null, null, Volts: value));
                    }
                    else if (basePrefix.StartsWith("power", StringComparison.Ordinal))
                    {
                        readings.Add(new ProxmoxSensorReading(
                            chip.Name, feature.Name, null, null, null, null, Watts: value));
                    }
                }
            }
        }
        return readings;
    }

    private static double? ReadFeatureDouble(JsonElement feature, string key) =>
        feature.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// V6.8.1 — reads the node's cumulative NIC error counter (rx_errs + tx_errs,
    /// drops excluded) from <c>/proc/net/dev</c> over SSH. The alert loop turns
    /// the rise between two reads into a spike level. No SSH ⇒ "not available"
    /// (the evaluator then never alerts on Network), matching the sensors path.
    /// </summary>
    public Task<ProxmoxNodeNetworkErrors> ReadNodeNetworkErrorsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeNetworkErrors(false, "SSH is not configured for this Proxmox host.", 0));

        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, "cat /proc/net/dev", cancellationToken);
            if (string.IsNullOrWhiteSpace(stdout))
                return Task.FromResult(new ProxmoxNodeNetworkErrors(false, "Could not read /proc/net/dev.", 0));

            return Task.FromResult(new ProxmoxNodeNetworkErrors(true, null, ParseNetDevErrors(stdout)));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox /proc/net/dev read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeNetworkErrors(false, ex.Message, 0));
        }
    }

    /// <summary>Sums the rx/tx <em>error</em> columns across physical interfaces
    /// in a <c>/proc/net/dev</c> dump. Each data line is
    /// <c>iface: rx_bytes rx_packets rx_errs rx_drop … tx_bytes tx_packets tx_errs
    /// tx_drop …</c> (16 numeric columns). Only true errors (rx_errs + tx_errs)
    /// are counted — <em>drops</em> are deliberately excluded because on a
    /// bridged Proxmox node the rx_dropped counter climbs for entirely benign
    /// reasons (frames not addressed to the host), which would otherwise fire a
    /// constant false NIC alert. Loopback and virtual interfaces
    /// (veth/tap/fw*/bridge plumbing, docker) are skipped. Never throws — a
    /// malformed line is skipped.</summary>
    internal static long ParseNetDevErrors(string netDev)
    {
        long total = 0;
        foreach (var rawLine in netDev.Split('\n'))
        {
            var colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;   // header lines have no "iface:" prefix

            var iface = rawLine[..colon].Trim();
            if (iface.Length == 0 || IsVirtualInterface(iface)) continue;

            var fields = rawLine[(colon + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Columns (0-based): 2=rx_errs, 10=tx_errs (drops at 3/11 are excluded).
            if (fields.Length < 11) continue;

            total += ParseLong(fields[2]) + ParseLong(fields[10]);
        }
        return total;
    }

    private static bool IsVirtualInterface(string iface) =>
        iface == "lo"
        || iface.StartsWith("veth", StringComparison.Ordinal)
        || iface.StartsWith("tap", StringComparison.Ordinal)
        || iface.StartsWith("fwbr", StringComparison.Ordinal)
        || iface.StartsWith("fwpr", StringComparison.Ordinal)
        || iface.StartsWith("fwln", StringComparison.Ordinal)
        || iface.StartsWith("docker", StringComparison.Ordinal)
        || iface.StartsWith("br-", StringComparison.Ordinal);

    private static long ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ── V6.8.2 — deep telemetry collectors ──────────────────────────────────
    //
    // Each is an independent SSH round-trip behind its own capability check,
    // mirroring the sensors / net-dev paths above: a missing source comes back as
    // Available=false with a human reason, never an exception. The two-sample
    // collectors (CPU, disk IO, network) take both samples in a single command
    // with a `sleep 1` between them, so one SSH session yields a rate. Sections
    // are split on this marker; the pure parsers are `internal static` so they're
    // unit-testable without SSH.

    private const string SampleMarker = "===STASHBOARD-SAMPLE===";
    private const double SampleIntervalSeconds = 1.0;

    /// <summary>
    /// V6.8.2 — per-core CPU utilisation + steal (<c>/proc/stat</c>, two samples)
    /// and MemAvailable (<c>/proc/meminfo</c>). One SSH command emits meminfo, the
    /// first stat sample, a marker, a 1s sleep, then the second stat sample.
    /// </summary>
    public Task<ProxmoxNodeCpuStats> ReadNodeCpuStatsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeCpuStats(false, "SSH is not configured for this Proxmox host.", [], null, null));

        var command =
            $"cat /proc/meminfo; echo {SampleMarker}; cat /proc/stat; echo {SampleMarker}; " +
            $"sleep {SampleIntervalSeconds:0}; cat /proc/stat";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var sections = SplitSamples(stdout, 3);
            if (sections is null)
                return Task.FromResult(new ProxmoxNodeCpuStats(false, "Could not read /proc/stat + /proc/meminfo.", [], null, null));

            var memAvailable = ParseMemAvailable(sections[0]);
            var (cores, steal) = ParseCpuStats(sections[1], sections[2]);
            return Task.FromResult(new ProxmoxNodeCpuStats(true, null, cores, steal, memAvailable));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox CPU stats read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeCpuStats(false, ex.Message, [], null, null));
        }
    }

    /// <summary>Reads <c>MemAvailable</c> (kB) from a <c>/proc/meminfo</c> dump and
    /// returns it in bytes, or <c>null</c> when the line is absent.</summary>
    internal static long? ParseMemAvailable(string meminfo)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // "MemAvailable: 12345 kB"
            if (fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                return kb * 1024;
        }
        return null;
    }

    /// <summary>Diffs two <c>/proc/stat</c> samples into per-core utilisation +
    /// steal and the node-aggregate steal. Each <c>cpuN</c> line is
    /// <c>user nice system idle iowait irq softirq steal guest guest_nice</c>;
    /// busy% = 100·(1 − idleΔ/totalΔ) with idle = idle + iowait, and steal% =
    /// 100·stealΔ/totalΔ. The bare <c>cpu</c> aggregate line drives the aggregate
    /// steal. Never throws — a malformed line is skipped.</summary>
    internal static (IReadOnlyList<ProxmoxCpuCoreStat> Cores, double? StealPercent) ParseCpuStats(
        string sample1, string sample2)
    {
        var first = ParseProcStat(sample1);
        var second = ParseProcStat(sample2);

        var cores = new List<ProxmoxCpuCoreStat>();
        double? aggregateSteal = null;

        foreach (var (label, b) in second)
        {
            if (!first.TryGetValue(label, out var a)) continue;
            var (util, steal) = CpuDelta(a, b);
            if (label == "cpu")
            {
                aggregateSteal = steal;
            }
            else if (label.Length > 3 && int.TryParse(label.AsSpan(3), out var core))
            {
                cores.Add(new ProxmoxCpuCoreStat(core, util, steal));
            }
        }
        cores.Sort((x, y) => x.Core.CompareTo(y.Core));
        return (cores, aggregateSteal);
    }

    /// <summary>(util%, steal%) between two per-cpu field arrays.</summary>
    private static (double Util, double Steal) CpuDelta(long[] a, long[] b)
    {
        long totalA = 0, totalB = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++) { totalA += a[i]; totalB += b[i]; }
        var totalDelta = totalB - totalA;
        if (totalDelta <= 0) return (0, 0);

        // idle = field[3] (idle) + field[4] (iowait); steal = field[7].
        long IdleAt(long[] f) => (f.Length > 3 ? f[3] : 0) + (f.Length > 4 ? f[4] : 0);
        long StealAt(long[] f) => f.Length > 7 ? f[7] : 0;

        var idleDelta = IdleAt(b) - IdleAt(a);
        var stealDelta = StealAt(b) - StealAt(a);
        var util = 100.0 * (totalDelta - idleDelta) / totalDelta;
        var steal = 100.0 * stealDelta / totalDelta;
        return (Math.Clamp(util, 0, 100), Math.Clamp(steal, 0, 100));
    }

    /// <summary>Parses the <c>cpu*</c> lines of a <c>/proc/stat</c> dump into a
    /// label → numeric-fields map.</summary>
    private static Dictionary<string, long[]> ParseProcStat(string sample)
    {
        var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var rawLine in sample.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) continue;   // need at least up to iowait
            var label = fields[0];
            var values = new long[fields.Length - 1];
            for (var i = 1; i < fields.Length; i++) values[i - 1] = ParseLong(fields[i]);
            map[label] = values;
        }
        return map;
    }

    /// <summary>
    /// V6.8.2 — per-disk IO from two <c>/proc/diskstats</c> samples.
    /// </summary>
    public Task<ProxmoxNodeDiskIo> ReadNodeDiskIoAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeDiskIo(false, "SSH is not configured for this Proxmox host.", []));

        var command =
            $"cat /proc/diskstats; echo {SampleMarker}; sleep {SampleIntervalSeconds:0}; cat /proc/diskstats";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var sections = SplitSamples(stdout, 2);
            if (sections is null)
                return Task.FromResult(new ProxmoxNodeDiskIo(false, "Could not read /proc/diskstats.", []));

            var disks = ParseDiskIo(sections[0], sections[1], SampleIntervalSeconds);
            return Task.FromResult(disks.Count == 0
                ? new ProxmoxNodeDiskIo(false, "No physical disks reported by /proc/diskstats.", [])
                : new ProxmoxNodeDiskIo(true, null, disks));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox disk IO read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeDiskIo(false, ex.Message, []));
        }
    }

    /// <summary>Diffs two <c>/proc/diskstats</c> samples over <paramref name="dt"/>
    /// seconds into per-disk throughput / IOPS / await. Columns (0-based):
    /// 2=name, 3=reads, 5=sectors_read, 6=ms_reading, 7=writes, 9=sectors_written,
    /// 10=ms_writing. Sectors are 512 B. Partitions and virtual devices
    /// (loop/ram/dm/sr) are skipped — only whole physical disks. Never throws.</summary>
    internal static IReadOnlyList<ProxmoxDiskIoStat> ParseDiskIo(string sample1, string sample2, double dt)
    {
        if (dt <= 0) dt = 1.0;
        var first = ParseDiskStats(sample1);
        var second = ParseDiskStats(sample2);

        var result = new List<ProxmoxDiskIoStat>();
        foreach (var (name, b) in second)
        {
            if (IsPartitionOrVirtualDisk(name)) continue;
            if (!first.TryGetValue(name, out var a)) continue;

            var reads = b[0] - a[0];
            var sectorsRead = b[1] - a[1];
            var msReading = b[2] - a[2];
            var writes = b[3] - a[3];
            var sectorsWritten = b[4] - a[4];
            var msWriting = b[5] - a[5];

            // Skip totally-idle disks with no deltas to avoid a wall of zeros.
            if (reads == 0 && writes == 0 && sectorsRead == 0 && sectorsWritten == 0) continue;

            result.Add(new ProxmoxDiskIoStat(
                name,
                ReadBytesPerSec: sectorsRead * 512 / dt,
                WriteBytesPerSec: sectorsWritten * 512 / dt,
                ReadIops: reads / dt,
                WriteIops: writes / dt,
                ReadAwaitMs: reads > 0 ? (double)msReading / reads : null,
                WriteAwaitMs: writes > 0 ? (double)msWriting / writes : null));
        }
        result.Sort((x, y) => string.CompareOrdinal(x.Device, y.Device));
        return result;
    }

    /// <summary>Parses <c>/proc/diskstats</c> into name → [reads, sectors_read,
    /// ms_reading, writes, sectors_written, ms_writing].</summary>
    private static Dictionary<string, long[]> ParseDiskStats(string sample)
    {
        var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var rawLine in sample.Split('\n'))
        {
            var fields = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 11) continue;
            var name = fields[2];
            map[name] = new[]
            {
                ParseLong(fields[3]),   // reads completed
                ParseLong(fields[5]),   // sectors read
                ParseLong(fields[6]),   // ms reading
                ParseLong(fields[7]),   // writes completed
                ParseLong(fields[9]),   // sectors written
                ParseLong(fields[10]),  // ms writing
            };
        }
        return map;
    }

    private static bool IsPartitionOrVirtualDisk(string name)
    {
        if (name.StartsWith("loop", StringComparison.Ordinal)
            || name.StartsWith("ram", StringComparison.Ordinal)
            || name.StartsWith("dm-", StringComparison.Ordinal)
            || name.StartsWith("sr", StringComparison.Ordinal)
            || name.StartsWith("fd", StringComparison.Ordinal)
            || name.StartsWith("zram", StringComparison.Ordinal))
            return true;

        // Partitions: sdX has trailing digits (sda1); nvme/mmcblk use a `p<n>`
        // suffix (nvme0n1p1). Whole disks (sda, nvme0n1, vda, mmcblk0) are kept.
        if ((name.StartsWith("sd", StringComparison.Ordinal)
             || name.StartsWith("vd", StringComparison.Ordinal)
             || name.StartsWith("hd", StringComparison.Ordinal))
            && char.IsDigit(name[^1]))
            return true;

        if ((name.StartsWith("nvme", StringComparison.Ordinal)
             || name.StartsWith("mmcblk", StringComparison.Ordinal))
            && name.Contains('p') && char.IsDigit(name[^1])
            && name.LastIndexOf('p') > name.Length - 4)
            return true;

        return false;
    }

    /// <summary>
    /// V6.8.2 — LVM-thin pool fill levels from <c>lvs</c>. Emits the JSON report
    /// (stable across LVM versions); the sentinel <c>NOLVS</c> covers a host
    /// without LVM tooling.
    /// </summary>
    public Task<ProxmoxNodeThinPools> ReadNodeThinPoolsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeThinPools(false, "SSH is not configured for this Proxmox host.", []));

        const string command =
            "if command -v lvs >/dev/null 2>&1; then " +
            "lvs --reportformat json --units b --nosuffix " +
            "-o lv_name,vg_name,lv_size,data_percent,metadata_percent,lv_attr 2>/dev/null; " +
            "else echo NOLVS; fi";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var output = stdout.Trim();
            if (output is "NOLVS" or "")
                return Task.FromResult(new ProxmoxNodeThinPools(
                    false, "LVM (lvs) is not installed on this host — no thin-pool data.", []));

            var pools = ParseThinPools(output);
            return Task.FromResult(pools.Count == 0
                ? new ProxmoxNodeThinPools(false, "No LVM-thin pools found on this host.", [])
                : new ProxmoxNodeThinPools(true, null, pools));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox thin-pool read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeThinPools(false, ex.Message, []));
        }
    }

    /// <summary>Parses <c>lvs --reportformat json</c> output. The shape is
    /// <c>{ "report": [ { "lv": [ { "lv_name": …, "data_percent": "…" } ] } ] }</c>.
    /// A thin pool is an LV whose <c>lv_attr</c> starts with <c>t</c> (or, as a
    /// fallback, any LV that reports a <c>data_percent</c>). Never throws past the
    /// caller's catch.</summary>
    internal static IReadOnlyList<ProxmoxThinPool> ParseThinPools(string json)
    {
        var pools = new List<ProxmoxThinPool>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("report", out var report) || report.ValueKind != JsonValueKind.Array)
            return pools;

        foreach (var group in report.EnumerateArray())
        {
            if (!group.TryGetProperty("lv", out var lvs) || lvs.ValueKind != JsonValueKind.Array) continue;
            foreach (var lv in lvs.EnumerateArray())
            {
                var attr = StrProp(lv, "lv_attr");
                var dataPct = DblProp(lv, "data_percent");
                var isThinPool = (attr is { Length: > 0 } && (attr[0] is 't' or 'T')) || dataPct is not null;
                if (!isThinPool) continue;

                var name = StrProp(lv, "lv_name");
                if (string.IsNullOrEmpty(name)) continue;
                pools.Add(new ProxmoxThinPool(
                    name,
                    StrProp(lv, "vg_name") ?? "",
                    LongProp(lv, "lv_size"),
                    dataPct,
                    DblProp(lv, "metadata_percent")));
            }
        }
        pools.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        return pools;

        static string? StrProp(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? DblProp(JsonElement e, string name)
        {
            var s = StrProp(e, name);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        static long? LongProp(JsonElement e, string name)
        {
            var s = StrProp(e, name);
            return long.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null;
        }
    }

    /// <summary>
    /// V6.8.2 — per-interface throughput / errors from two <c>/proc/net/dev</c>
    /// samples, with link speed/duplex/state from <c>/sys/class/net</c>. The
    /// single command emits both net/dev samples (with a 1s sleep) then a link
    /// section: one <c>iface operstate speed duplex</c> line per interface.
    /// </summary>
    public Task<ProxmoxNodeInterfaceStats> ReadNodeInterfaceStatsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxNodeInterfaceStats(false, "SSH is not configured for this Proxmox host.", []));

        var command =
            $"cat /proc/net/dev; echo {SampleMarker}; sleep {SampleIntervalSeconds:0}; cat /proc/net/dev; " +
            $"echo {SampleMarker}; " +
            "for i in /sys/class/net/*; do n=$(basename \"$i\"); " +
            "echo \"$n $(cat $i/operstate 2>/dev/null) $(cat $i/speed 2>/dev/null) $(cat $i/duplex 2>/dev/null)\"; done";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var sections = SplitSamples(stdout, 3);
            if (sections is null)
                return Task.FromResult(new ProxmoxNodeInterfaceStats(false, "Could not read /proc/net/dev.", []));

            var ifaces = ParseInterfaceStats(sections[0], sections[1], SampleIntervalSeconds, sections[2]);
            return Task.FromResult(ifaces.Count == 0
                ? new ProxmoxNodeInterfaceStats(false, "No physical interfaces reported.", [])
                : new ProxmoxNodeInterfaceStats(true, null, ifaces));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox interface stats read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxNodeInterfaceStats(false, ex.Message, []));
        }
    }

    /// <summary>Diffs two <c>/proc/net/dev</c> samples over <paramref name="dt"/>
    /// seconds into per-interface RX/TX rates + cumulative error/drop counters,
    /// joined with the <c>/sys/class/net</c> link section
    /// (<c>iface operstate speed duplex</c> per line). Columns after "iface:":
    /// 0=rx_bytes 2=rx_errs 3=rx_drop … 8=tx_bytes 10=tx_errs 11=tx_drop. Virtual
    /// interfaces (lo/veth/tap/fw*/docker/br-) are skipped, matching
    /// <see cref="ParseNetDevErrors"/>. Never throws.</summary>
    internal static IReadOnlyList<ProxmoxInterfaceStat> ParseInterfaceStats(
        string sample1, string sample2, double dt, string? linkSection)
    {
        if (dt <= 0) dt = 1.0;
        var first = ParseNetDev(sample1);
        var second = ParseNetDev(sample2);
        var links = ParseLinkSection(linkSection);

        var result = new List<ProxmoxInterfaceStat>();
        foreach (var (iface, b) in second)
        {
            if (IsVirtualInterface(iface)) continue;
            if (!first.TryGetValue(iface, out var a)) continue;

            var rxBytes = b[0] - a[0];
            var txBytes = b[3] - a[3];
            links.TryGetValue(iface, out var link);
            result.Add(new ProxmoxInterfaceStat(
                iface,
                RxBytesPerSec: Math.Max(0, rxBytes) / dt,
                TxBytesPerSec: Math.Max(0, txBytes) / dt,
                RxErrors: b[1],
                TxErrors: b[4],
                RxDropped: b[2],
                TxDropped: b[5],
                SpeedMbps: link.Speed,
                Duplex: link.Duplex,
                OperState: link.OperState));
        }
        result.Sort((x, y) => string.CompareOrdinal(x.Iface, y.Iface));
        return result;
    }

    /// <summary>Parses <c>/proc/net/dev</c> into iface → [rx_bytes, rx_errs,
    /// rx_drop, tx_bytes, tx_errs, tx_drop].</summary>
    private static Dictionary<string, long[]> ParseNetDev(string sample)
    {
        var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var rawLine in sample.Split('\n'))
        {
            var colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;
            var iface = rawLine[..colon].Trim();
            if (iface.Length == 0) continue;
            var f = rawLine[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 12) continue;
            map[iface] = new[]
            {
                ParseLong(f[0]),   // rx_bytes
                ParseLong(f[2]),   // rx_errs
                ParseLong(f[3]),   // rx_drop
                ParseLong(f[8]),   // tx_bytes
                ParseLong(f[10]),  // tx_errs
                ParseLong(f[11]),  // tx_drop
            };
        }
        return map;
    }

    private static Dictionary<string, (long? Speed, string? Duplex, string? OperState)> ParseLinkSection(string? section)
    {
        var map = new Dictionary<string, (long?, string?, string?)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(section)) return map;
        foreach (var rawLine in section.Split('\n'))
        {
            var f = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            var iface = f[0];
            var operState = f.Length > 1 ? NullIfEmpty(f[1]) : null;
            long? speed = f.Length > 2 && long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0
                ? s : null;   // /sys reports -1 for a down link
            var duplex = f.Length > 3 ? NullIfEmpty(f[3]) : null;
            map[iface] = (speed, duplex, operState);
        }
        return map;

        static string? NullIfEmpty(string v) =>
            string.IsNullOrWhiteSpace(v) || v == "unknown" ? null : v;
    }

    /// <summary>
    /// V6.8.2 — one disk's last SMART self-test result + critical counters via
    /// <c>smartctl -l selftest -A</c>. The sentinel <c>NOSMARTCTL</c> covers a
    /// host without <c>smartmontools</c>.
    /// </summary>
    public Task<ProxmoxDiskSelfTest> ReadNodeDiskSelfTestAsync(
        ProxmoxConnectionProfile profile, string devPath, CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return Task.FromResult(new ProxmoxDiskSelfTest(false, "SSH is not configured for this Proxmox host.", null, null, null, null, null, null, null));

        // devPath is an absolute /dev path from the API disk list; quote it.
        var quoted = "'" + devPath.Replace("'", "") + "'";
        var command =
            "if command -v smartctl >/dev/null 2>&1; then " +
            $"smartctl -l selftest -A {quoted} 2>/dev/null; else echo NOSMARTCTL; fi";
        try
        {
            var (_, stdout, _) = RunCommand(profile.Ssh, command, cancellationToken);
            var output = stdout.Trim();
            if (output is "NOSMARTCTL" or "")
                return Task.FromResult(new ProxmoxDiskSelfTest(
                    false, "smartctl is not installed on this host (apt install smartmontools).", null, null, null, null, null, null, null));

            return Task.FromResult(ParseSelfTest(output));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Proxmox SMART self-test read failed for host {Host}", profile.Ssh.Host);
            return Task.FromResult(new ProxmoxDiskSelfTest(false, ex.Message, null, null, null, null, null, null, null));
        }
    }

    /// <summary>Parses <c>smartctl -l selftest -A</c> output: the self-test log's
    /// first (most recent) data row and the critical ATA attributes
    /// (Reallocated_Sector_Ct, Current_Pending_Sector, Offline_Uncorrectable,
    /// Power_On_Hours). The self-test row is
    /// <c># 1  Extended offline  Completed without error  00%  12345  -</c>; we
    /// take the test type, the status text, and the lifetime hours column. Never
    /// throws.</summary>
    internal static ProxmoxDiskSelfTest ParseSelfTest(string text)
    {
        string? testType = null, status = null;
        long? testHours = null, powerOnHours = null, reallocated = null, pending = null, uncorrectable = null;

        var lines = text.Split('\n');
        var inSelfTestLog = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Self-test log: take the first "# N" row (most recent).
            if (line.StartsWith("Num", StringComparison.Ordinal) && line.Contains("Test_Description"))
            {
                inSelfTestLog = true;
                continue;
            }
            if (inSelfTestLog && status is null && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                // "# 1  Extended offline    Completed without error   00%     12345         -"
                var body = line.TrimStart()[1..].TrimStart();   // drop the leading '#'
                // First token is the index number; strip it.
                var sp = body.IndexOf(' ');
                if (sp > 0) body = body[sp..].TrimStart();
                ParseSelfTestRow(body, ref testType, ref status, ref testHours);
                continue;
            }

            // Attribute table rows: id name flag value worst thresh type updated when_failed raw.
            ScanCriticalAttribute(line, "Reallocated_Sector_Ct", ref reallocated);
            ScanCriticalAttribute(line, "Current_Pending_Sector", ref pending);
            ScanCriticalAttribute(line, "Offline_Uncorrectable", ref uncorrectable);
            ScanCriticalAttribute(line, "Power_On_Hours", ref powerOnHours);
        }

        return new ProxmoxDiskSelfTest(
            true, null, testType, status, testHours, powerOnHours, reallocated, pending, uncorrectable);
    }

    /// <summary>Splits a self-test row body (everything after "# N") into the test
    /// type, the status text, and the LifeTime hours. The status column always
    /// contains a known verb, so we anchor on that: the words before it are the
    /// test type, the words from the verb up to the remaining-% column are the
    /// status, and the first integer token after that column is the lifetime
    /// hours.</summary>
    private static void ParseSelfTestRow(string body, ref string? testType, ref string? status, ref long? testHours)
    {
        foreach (var verb in SelfTestVerbs)
        {
            var idx = body.IndexOf(verb, StringComparison.Ordinal);
            if (idx < 0) continue;
            testType = body[..idx].Trim();

            var rest = body[idx..];
            var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // The remaining-% column (e.g. "00%" / "100%") ends the status text.
            var pctIdx = Array.FindIndex(tokens, t => t.EndsWith('%'));
            if (pctIdx > 0)
            {
                status = string.Join(' ', tokens[..pctIdx]);
                for (var i = pctIdx + 1; i < tokens.Length; i++)
                    if (long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    {
                        testHours = h;
                        break;
                    }
            }
            else
            {
                status = rest.Trim();
            }
            return;
        }
        testType = body.Trim();
    }

    private static readonly string[] SelfTestVerbs =
        ["Completed", "Aborted", "Interrupted", "Self-test routine in progress", "Fatal", "Unknown", "Failed"];

    /// <summary>Reads the RAW_VALUE (last column) of an ATA attribute row matching
    /// <paramref name="attrName"/>, when not already found.</summary>
    private static void ScanCriticalAttribute(string line, string attrName, ref long? target)
    {
        if (target is not null) return;
        if (!line.Contains(attrName, StringComparison.Ordinal)) return;
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0) return;
        // RAW_VALUE is the last column; some firmwares append "(…)" notes — take
        // the leading integer of the last token.
        var rawToken = fields[^1];
        var digits = new string(rawToken.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0 && long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            target = v;
    }

    /// <summary>Splits a multi-sample command's stdout on
    /// <see cref="SampleMarker"/> and returns the trimmed sections, or
    /// <c>null</c> when fewer than <paramref name="expected"/> were produced
    /// (e.g. the command errored before the marker).</summary>
    private static string[]? SplitSamples(string stdout, int expected)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var parts = stdout.Split(SampleMarker);
        if (parts.Length < expected) return null;
        var sections = new string[expected];
        for (var i = 0; i < expected; i++) sections[i] = parts[i].Trim('\r', '\n', ' ', '\t');
        return sections;
    }

    private static (int ExitStatus, string Stdout, string Stderr) RunCommand(
        DockerSshCredentials credentials, string commandText, CancellationToken cancellationToken)
    {
        var keyFile = SshPrivateKeyLoader.Load(credentials);
        using var ssh = new SshClient(credentials.Host, credentials.Port, credentials.Username, keyFile)
        {
            KeepAliveInterval = Timeout.InfiniteTimeSpan,
        };
        ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds);
        ssh.Connect();
        try
        {
            using var cmd = ssh.CreateCommand(commandText);
            cmd.CommandTimeout = TimeSpan.FromSeconds(CommandTimeoutSeconds);
            using var registration = cancellationToken.Register(() => { try { cmd.CancelAsync(); } catch { /* best effort */ } });
            var stdout = cmd.Execute();
            return (cmd.ExitStatus ?? -1, stdout, cmd.Error);
        }
        finally
        {
            try { ssh.Disconnect(); } catch { /* idempotent */ }
        }
    }
}
