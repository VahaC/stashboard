namespace Stashboard.Core.Abstractions;

/// <summary>V6.0 — result of inspecting one LXC's pending updates over SSH.</summary>
/// <param name="PendingUpdates">Upgradable package count, or <c>null</c> when
/// it couldn't be determined.</param>
/// <param name="Error">Reason the count is <c>null</c> (not Debian-based,
/// <c>pct exec</c> failed, …), or <c>null</c> on success.</param>
public sealed record ProxmoxGuestInspection(int? PendingUpdates, string? Error);

/// <summary>V6.8 — one reading parsed from <c>sensors -j</c> on the Proxmox host.
/// Exactly one of <paramref name="TempC"/> / <paramref name="Rpm"/> /
/// <paramref name="Volts"/> / <paramref name="Watts"/> is set (V6.8.2 added the
/// voltage / power inputs). For temperatures, <paramref name="HighC"/> /
/// <paramref name="CritC"/> are the high/critical thresholds where the chip
/// exposes them.</summary>
public sealed record ProxmoxSensorReading(
    string Chip,
    string Label,
    double? TempC,
    double? HighC,
    double? CritC,
    double? Rpm,
    double? Volts = null,
    double? Watts = null);

/// <summary>V6.8 — the node's thermal/fan snapshot. <paramref name="Available"/>
/// is <c>false</c> (with a reason in <paramref name="Error"/>) when SSH isn't
/// configured or <c>lm-sensors</c> isn't installed — the card then shows a
/// "not available" marker rather than failing.</summary>
public sealed record ProxmoxNodeSensors(
    bool Available,
    string? Error,
    IReadOnlyList<ProxmoxSensorReading> Readings);

/// <summary>V6.8.1 — the node's cumulative NIC <em>error</em> counter (rx_errs +
/// tx_errs), summed across physical interfaces from <c>/proc/net/dev</c> over SSH
/// (the Proxmox REST API exposes no per-interface error counters). Drops are
/// intentionally excluded — on a bridged Proxmox node they climb for benign
/// reasons. <paramref name="Available"/> is <c>false</c> (with a reason in
/// <paramref name="Error"/>) when SSH isn't configured or the read failed — the
/// alert evaluator then treats Network as "n/a" and never fires, rather than
/// reading a spurious spike. <paramref name="TotalErrors"/> is a monotonic
/// counter; the alert loop stores it as a baseline and alerts on the rise
/// between evaluations.</summary>
public sealed record ProxmoxNodeNetworkErrors(
    bool Available,
    string? Error,
    long TotalErrors);

// ── V6.8.2 — PVE node deep telemetry (SSH collectors) ───────────────────────

/// <summary>V6.8.2 — one logical CPU core's utilisation between two
/// <c>/proc/stat</c> samples. <paramref name="UtilPercent"/> is busy time
/// (100 − idle%); <paramref name="StealPercent"/> is the hypervisor-stolen share
/// (≈0 on bare metal, meaningful when the node is itself a guest).</summary>
public sealed record ProxmoxCpuCoreStat(
    int Core,
    double UtilPercent,
    double StealPercent);

/// <summary>V6.8.2 — per-core CPU utilisation + steal and MemAvailable, read over
/// SSH (the REST API gives only aggregate CPU + iowait, and reports memory
/// <c>free</c>, not <c>available</c>). <paramref name="StealPercent"/> is the
/// node-aggregate steal. <paramref name="Available"/> is <c>false</c> (with a
/// reason in <paramref name="Error"/>) when SSH isn't configured or the read
/// failed — the tab then shows "not available".</summary>
public sealed record ProxmoxNodeCpuStats(
    bool Available,
    string? Error,
    IReadOnlyList<ProxmoxCpuCoreStat> Cores,
    double? StealPercent,
    long? MemAvailableBytes);

/// <summary>V6.8.2 — one physical disk's IO between two <c>/proc/diskstats</c>
/// samples. Throughput is bytes/s; IOPS is operations/s; the await figures are
/// average ms per completed op over the interval (<c>null</c> when there were no
/// ops to average).</summary>
public sealed record ProxmoxDiskIoStat(
    string Device,
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double ReadIops,
    double WriteIops,
    double? ReadAwaitMs,
    double? WriteAwaitMs);

/// <summary>V6.8.2 — per-disk IO throughput / IOPS / latency from two
/// <c>/proc/diskstats</c> samples over SSH (not in the REST API). Backs the
/// Storage/SMART tab's IO section.</summary>
public sealed record ProxmoxNodeDiskIo(
    bool Available,
    string? Error,
    IReadOnlyList<ProxmoxDiskIoStat> Disks);

/// <summary>V6.8.2 — one LVM-thin pool's fill level from <c>lvs</c>.
/// <paramref name="DataPercent"/> / <paramref name="MetadataPercent"/> are the
/// allocated-data / allocated-metadata percentages; a pool nearing 100% data (or
/// 100% metadata, which wedges the pool) is what the UI badges.</summary>
public sealed record ProxmoxThinPool(
    string Name,
    string VolumeGroup,
    long? SizeBytes,
    double? DataPercent,
    double? MetadataPercent);

/// <summary>V6.8.2 — LVM-thin pool fill levels from <c>lvs</c> over SSH.
/// <paramref name="Available"/> is <c>false</c> when SSH isn't configured,
/// <c>lvs</c> isn't installed, or the host has no thin pools — the section then
/// shows "not available" rather than failing.</summary>
public sealed record ProxmoxNodeThinPools(
    bool Available,
    string? Error,
    IReadOnlyList<ProxmoxThinPool> Pools);

/// <summary>V6.8.2 — one interface's throughput + counters + link between two
/// <c>/proc/net/dev</c> samples, with link speed/duplex/state from
/// <c>/sys/class/net</c>. Rates are bytes/s; the error/drop figures are the
/// cumulative counters at the second sample.</summary>
public sealed record ProxmoxInterfaceStat(
    string Iface,
    double RxBytesPerSec,
    double TxBytesPerSec,
    long RxErrors,
    long TxErrors,
    long RxDropped,
    long TxDropped,
    long? SpeedMbps,
    string? Duplex,
    string? OperState);

/// <summary>V6.8.2 — per-interface throughput / errors / link from two
/// <c>/proc/net/dev</c> samples + <c>/sys/class/net</c> over SSH. Replaces the
/// node-aggregate-only throughput on the Network tab (the REST API has no
/// per-interface counters).</summary>
public sealed record ProxmoxNodeInterfaceStats(
    bool Available,
    string? Error,
    IReadOnlyList<ProxmoxInterfaceStat> Interfaces);

/// <summary>V6.8.2 — one disk's last SMART self-test + the badge-worthy critical
/// counters from <c>smartctl -l selftest -A</c>. Every field is nullable so a
/// disk that has never been self-tested (or whose firmware omits a counter) still
/// renders. <paramref name="LastTestPowerOnHours"/> is the lifetime hour-count at
/// the time of the last test; <paramref name="PowerOnHours"/> is the current one
/// (their difference is the test's age).</summary>
public sealed record ProxmoxDiskSelfTest(
    bool Available,
    string? Error,
    string? LastTestType,
    string? LastTestStatus,
    long? LastTestPowerOnHours,
    long? PowerOnHours,
    long? ReallocatedSectors,
    long? PendingSectors,
    long? UncorrectableSectors);

/// <summary>
/// V6.0 — reads the pending-update count inside a single LXC by SSHing to the
/// Proxmox host and running <c>pct exec &lt;vmid&gt; -- apt list --upgradable</c>.
/// The Proxmox REST API has no command-exec endpoint for LXC, so this SSH path
/// is the only way to obtain a per-container count. Behind an interface so the
/// orchestrator is testable without SSH.
/// </summary>
public interface IProxmoxGuestInspector
{
    Task<ProxmoxGuestInspection> CountPendingUpdatesAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>Connects over SSH and runs a trivial command to verify the
    /// credentials work. Used by the connection-test endpoint.</summary>
    Task<bool> TestConnectionAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — reads the node's CPU/board temperatures and fan RPMs by
    /// running <c>sensors -j</c> over SSH and parsing the JSON. These are not in
    /// the Proxmox REST API, so SSH is the only source. Never throws — an
    /// unreachable host or a missing <c>lm-sensors</c> comes back as
    /// <see cref="ProxmoxNodeSensors.Available"/> = <c>false</c>.</summary>
    Task<ProxmoxNodeSensors> ReadNodeSensorsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8.1 — reads the node's cumulative NIC error counter by
    /// running <c>cat /proc/net/dev</c> over SSH and summing the rx/tx error
    /// columns across physical interfaces (drops excluded — they're benign on a
    /// bridged node). Not in the Proxmox REST API, so SSH is the only source.
    /// Never throws — no SSH or a failed read comes back as
    /// <see cref="ProxmoxNodeNetworkErrors.Available"/> = <c>false</c>, which the
    /// alert evaluator treats as "n/a" (never an alert).</summary>
    Task<ProxmoxNodeNetworkErrors> ReadNodeNetworkErrorsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    // ── V6.8.2 — deep telemetry collectors (each independent, each "not
    //    available" rather than throwing when its source is absent) ───────────

    /// <summary>V6.8.2 — per-core CPU utilisation + steal (two <c>/proc/stat</c>
    /// samples) plus MemAvailable (<c>/proc/meminfo</c>) over SSH.</summary>
    Task<ProxmoxNodeCpuStats> ReadNodeCpuStatsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8.2 — per-disk IO throughput / IOPS / latency from two
    /// <c>/proc/diskstats</c> samples over SSH.</summary>
    Task<ProxmoxNodeDiskIo> ReadNodeDiskIoAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8.2 — LVM-thin pool fill levels from <c>lvs</c> over SSH.</summary>
    Task<ProxmoxNodeThinPools> ReadNodeThinPoolsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8.2 — per-interface throughput / errors / link from two
    /// <c>/proc/net/dev</c> samples + <c>/sys/class/net</c> over SSH.</summary>
    Task<ProxmoxNodeInterfaceStats> ReadNodeInterfaceStatsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8.2 — one disk's last SMART self-test + critical counters from
    /// <c>smartctl -l selftest -A</c> over SSH.</summary>
    Task<ProxmoxDiskSelfTest> ReadNodeDiskSelfTestAsync(
        ProxmoxConnectionProfile profile, string devPath, CancellationToken cancellationToken = default);
}
