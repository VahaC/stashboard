namespace Stashboard.Core.Abstractions;

/// <summary>V6.0 — one LXC container as reported by
/// <c>GET /nodes/{node}/lxc</c>. The resource fields beyond the identity come
/// from the same list response (no extra calls), so the dashboard card can show
/// useful detail even when SSH isn't configured.</summary>
/// <param name="VmId">Numeric container id.</param>
/// <param name="Name">Container hostname.</param>
/// <param name="IsRunning">Whether <c>status == "running"</c>.</param>
/// <param name="UptimeSeconds">Seconds since the container started (0 / null when stopped).</param>
/// <param name="CpuCores">Configured CPU core count.</param>
/// <param name="MemoryBytes">Memory limit in bytes (<c>maxmem</c>).</param>
/// <param name="DiskBytes">Root disk size in bytes (<c>maxdisk</c>).</param>
/// <param name="Tags">Proxmox tags, as the raw semicolon-separated string.</param>
public sealed record ProxmoxLxcSummary(
    int VmId,
    string Name,
    bool IsRunning,
    long? UptimeSeconds = null,
    int? CpuCores = null,
    long? MemoryBytes = null,
    long? DiskBytes = null,
    string? Tags = null);

/// <summary>V6.2 — one `net<n>` / `mp<n>` / `rootfs` line from an LXC config,
/// kept as the raw Proxmox option string (e.g.
/// <c>name=eth0,bridge=vmbr0,ip=dhcp</c>) plus its key (<c>net0</c>).</summary>
public sealed record ProxmoxLxcConfigLine(string Key, string Value);

/// <summary>V6.5 / V6.9 — the editable subset of an LXC's config, written via
/// <c>PUT …/lxc/{vmid}/config</c>. The scalar fields (V6.5) are each nullable: a
/// <c>null</c> means "leave this key untouched", so the client only sends the
/// fields the user actually changed. Memory / swap are in MiB (Proxmox's native
/// config unit), not bytes. V6.9 adds structured network / mount / rootfs edits
/// as <em>intentful</em> add/update/remove operations — see
/// <see cref="ProxmoxLxcNetChange"/> / <see cref="ProxmoxLxcMountChange"/> /
/// <see cref="ProxmoxLxcRootfsChange"/>. A <c>null</c> list means "no
/// network/mount changes", never "clear everything".</summary>
public sealed record ProxmoxLxcConfigUpdate(
    int? Cores = null,
    long? MemoryMib = null,
    long? SwapMib = null,
    string? Hostname = null,
    bool? Onboot = null,
    IReadOnlyList<ProxmoxLxcNetChange>? Networks = null,
    IReadOnlyList<ProxmoxLxcMountChange>? Mounts = null,
    ProxmoxLxcRootfsChange? Rootfs = null);

/// <summary>V6.9 — one intentful change to a <c>net&lt;n&gt;</c> interface.
/// <para><see cref="Key"/> is the numbered key (<c>net0</c>); leave it empty for
/// an <em>add</em> — the write path assigns the next free <c>net&lt;n&gt;</c>.
/// <see cref="Remove"/> routes the key into the Proxmox <c>delete=</c> list
/// instead of writing a value.</para>
/// <para>For an upsert, either set the structured fields (the write path formats
/// the exact Proxmox option line) <em>or</em> set <see cref="Raw"/> to supply the
/// whole option string verbatim (the advanced escape hatch for options Stashboard
/// does not model). <see cref="Extra"/> carries unknown-but-valid options parsed
/// off the original line so a structured edit never drops them.</para></summary>
public sealed record ProxmoxLxcNetChange(
    string Key,
    bool Remove = false,
    string? Raw = null,
    string? Name = null,
    string? Bridge = null,
    string? Ip = null,        // "dhcp" | "manual" | "x.x.x.x/nn"
    string? Gw = null,
    string? Ip6 = null,       // "dhcp" | "auto" | "manual" | "xxxx::/nn"
    string? Gw6 = null,
    int? Tag = null,          // VLAN tag
    bool? Firewall = null,
    int? Rate = null,         // MB/s
    int? Mtu = null,
    string? Hwaddr = null,    // MAC address
    bool? LinkDown = null,    // interface disabled when true
    string? Extra = null);

/// <summary>V6.9 — one intentful change to a secondary mount point
/// (<c>mp&lt;n&gt;</c>). <see cref="Key"/> empty ⇒ add (next free
/// <c>mp&lt;n&gt;</c>); <see cref="Remove"/> ⇒ <c>delete=mp&lt;n&gt;</c> (the
/// config entry only — underlying storage content is not touched).
/// <see cref="Volume"/> is the positional storage/source token
/// (<c>local-lvm:8</c> for a new volume, <c>local-lvm:vm-101-disk-1</c> for an
/// existing one, or a host path for a bind mount).</summary>
public sealed record ProxmoxLxcMountChange(
    string Key,
    bool Remove = false,
    string? Raw = null,
    string? Volume = null,
    string? MountPoint = null,   // mp=
    string? Size = null,         // size=8G
    bool? ReadOnly = null,       // ro=
    bool? Backup = null,         // backup=
    bool? Quota = null,          // quota=
    bool? Acl = null,            // acl=
    bool? Shared = null,         // shared=
    bool? Replicate = null,      // replicate=
    string? MountOptions = null, // mountoptions=
    string? Extra = null);

/// <summary>V6.9 — an edit to the container's <c>rootfs</c>. Unlike a
/// <see cref="ProxmoxLxcMountChange"/> it has no key and cannot be removed — only
/// the safe subset (size + selected storage flags) is editable. Set
/// <see cref="Raw"/> for the advanced verbatim path.</summary>
public sealed record ProxmoxLxcRootfsChange(
    string? Raw = null,
    string? Volume = null,
    string? Size = null,
    bool? ReadOnly = null,
    bool? Quota = null,
    bool? Acl = null,
    bool? Replicate = null,
    string? MountOptions = null,
    string? Extra = null);

/// <summary>
/// V6.13.1 — the minimum-viable spec for <strong>creating</strong> an LXC via
/// <c>POST /nodes/{node}/lxc</c>. Everything the create form collects, decoupled
/// from the on-wire form shape (the client builds that). Secrets
/// (<see cref="Password"/> / <see cref="SshPublicKeys"/>) are write-only — they
/// are sent to Proxmox and never read back. <see cref="Net0"/> reuses the V6.9
/// structured network model so the create form's interface row and the edit
/// form's row editor format identically (<see cref="ProxmoxLxcNetChange"/>).
/// </summary>
/// <param name="VmId">Numeric container id (100..999999999).</param>
/// <param name="OsTemplate">Template volume id, e.g. <c>local:vztmpl/debian-12.tar.zst</c>.</param>
/// <param name="RootfsStorage">Storage the root disk lands on, e.g. <c>local-lvm</c>.</param>
/// <param name="RootfsSizeGib">Root disk size in GiB.</param>
public sealed record ProxmoxLxcCreate(
    int VmId,
    string OsTemplate,
    string RootfsStorage,
    int RootfsSizeGib,
    string? Hostname = null,
    string? Description = null,
    string? Tags = null,
    string? Password = null,
    string? SshPublicKeys = null,
    int? Cores = null,
    long? MemoryMib = null,
    long? SwapMib = null,
    ProxmoxLxcNetChange? Net0 = null,
    bool Unprivileged = true,
    bool Onboot = false,
    bool Start = false,
    /// <summary>Enable the <c>nesting=1</c> feature (run Docker / nested LXC inside
    /// the container). Emitted on the <c>features</c> line.</summary>
    bool Nesting = false,
    /// <summary>DNS server(s) for the container (<c>nameserver</c>), space- or
    /// comma-separated; <c>null</c> ⇒ inherit the host's setting.</summary>
    string? Nameserver = null,
    /// <summary>DNS search domain (<c>searchdomain</c>); <c>null</c> ⇒ inherit.</summary>
    string? SearchDomain = null,
    /// <summary>After a successful create, register the container with the
    /// cluster's HA manager (<c>POST /cluster/ha/resources</c>). Best-effort —
    /// needs HA configured on the cluster.</summary>
    bool AddToHa = false,
    /// <summary>V8.1 — restore from a backup archive instead of provisioning from
    /// a template. When <c>true</c>, <see cref="OsTemplate"/> carries the backup
    /// <em>volid</em> (<c>storage:backup/vzdump-lxc-…</c>) and the client emits
    /// <c>restore=1</c>; the rootfs sizes and most config come from the archive, so
    /// <see cref="RootfsStorage"/> becomes the optional default <em>storage</em>
    /// override (Proxmox's <c>storage=</c>) and <see cref="RootfsSizeGib"/> is
    /// ignored. The template-only fields (<see cref="Password"/> /
    /// <see cref="SshPublicKeys"/> / <see cref="Net0"/> / DNS) do not apply.</summary>
    bool Restore = false,
    /// <summary>V8.1 — overwrite an existing container when restoring
    /// (<c>force=1</c>). Only meaningful with <see cref="Restore"/>; the target must
    /// be a stopped guest. Destructive — it replaces that container, so the UI
    /// double-confirms and the controller re-checks the stopped-guest guard.</summary>
    bool Force = false);

/// <summary>V6.13.1 — one container template (a <c>vztmpl</c> volume) from
/// <c>GET /nodes/{node}/storage/{storage}/content?content=vztmpl</c>, for the
/// create form's template dropdown. <see cref="Volid"/> is the value passed back
/// as <see cref="ProxmoxLxcCreate.OsTemplate"/>.</summary>
public sealed record ProxmoxTemplate(string Volid, string Storage, long? Size);

/// <summary>V8.1 — one restorable LXC backup archive (a <c>vzdump-lxc-*</c> volume)
/// from <c>GET /nodes/{node}/storage/{storage}/content?content=backup</c>, for the
/// restore form's archive dropdown. <see cref="Volid"/> is the value passed back as
/// <see cref="ProxmoxLxcCreate.OsTemplate"/> with <see cref="ProxmoxLxcCreate.Restore"/>
/// set. <see cref="VmId"/> is the guest id the archive was taken from (the restore
/// form offers it as the "original vmid" default); <see cref="CTime"/> is the backup's
/// creation unix second; <see cref="Format"/> is the archive format
/// (<c>tar.zst</c> / <c>tar.gz</c> / <c>tar.lzo</c>).</summary>
public sealed record ProxmoxBackup(
    string Volid, string Storage, int? VmId, long? CTime, long? Size, string? Format, string? Notes);

/// <summary>
/// V8.0 — the minimum-viable spec for <strong>cloning</strong> an existing LXC via
/// <c>POST /nodes/{node}/lxc/{vmid}/clone</c>. Mirrors the create spec's posture:
/// everything the clone form collects, decoupled from the on-wire form shape (the
/// client builds that). <see cref="NewVmId"/> is the destination guest id;
/// validation reuses the create vmid rules.
/// </summary>
/// <param name="NewVmId">Destination container id (100..999999999).</param>
/// <param name="Hostname">Hostname for the clone; <c>null</c> ⇒ Proxmox derives one.</param>
/// <param name="TargetStorage">Storage the clone's disks land on (a <em>full</em>
/// clone copies the volumes); <c>null</c> ⇒ keep the source's storage.</param>
/// <param name="Full">A full (independent copy) vs a linked (CoW, references the
/// source) clone. A linked clone needs the source to be a template or to have a
/// snapshot; Proxmox stays authoritative.</param>
/// <param name="SnapName">Clone from this source snapshot rather than the current
/// state; <c>null</c> ⇒ the live state.</param>
/// <param name="Description">Optional note set on the clone.</param>
public sealed record ProxmoxLxcClone(
    int NewVmId,
    string? Hostname = null,
    string? TargetStorage = null,
    bool Full = false,
    string? SnapName = null,
    string? Description = null);

/// <summary>V8.0 — one LXC snapshot from
/// <c>GET /nodes/{node}/lxc/{vmid}/snapshot</c>. Proxmox always includes a
/// synthetic <c>current</c> pseudo-entry for the live state; the client filters it
/// out so this list is purely real snapshots. <see cref="SnapTime"/> is a unix
/// second (<c>null</c> for the rare entry that lacks one); <see cref="Vmstate"/> is
/// <c>true</c> when the snapshot captured the running memory state.</summary>
public sealed record ProxmoxSnapshot(
    string Name,
    string? Description,
    long? SnapTime,
    string? Parent,
    bool Vmstate);

/// <summary>V6.2 — read model merging an LXC's static config
/// (<c>GET …/lxc/{vmid}/config</c>) with its live status
/// (<c>GET …/lxc/{vmid}/status/current</c>). Byte fields are normalised to
/// bytes (the config reports memory/swap in MiB; status reports bytes).</summary>
public sealed record ProxmoxLxcDetail(
    int VmId,
    string Status,
    // ── config (static) ──
    int? Cores,
    long? MemoryBytes,
    long? SwapBytes,
    string? Hostname,
    string? OsType,
    string? Arch,
    bool? Onboot,
    bool? Unprivileged,
    string? Features,
    IReadOnlyList<ProxmoxLxcConfigLine> Networks,
    IReadOnlyList<ProxmoxLxcConfigLine> Mounts,
    // ── status (live) ──
    double? CpuFraction,
    long? MemUsedBytes,
    long? MemMaxBytes,
    long? DiskUsedBytes,
    long? DiskMaxBytes,
    long? UptimeSeconds);

/// <summary>V6.3 — one RRD sample for an LXC (<c>GET …/lxc/{vmid}/rrddata</c>).
/// <paramref name="Time"/> is a unix second; the rate fields are bytes/s
/// averaged over the bucket; <paramref name="Cpu"/> is a 0..1 fraction. Any
/// field can be <c>null</c> for a bucket with no data.</summary>
public sealed record ProxmoxLxcRrdPoint(
    long Time,
    double? Cpu,
    long? MemUsed,
    long? MemMax,
    long? NetIn,
    long? NetOut,
    long? DiskRead,
    long? DiskWrite);

/// <summary>V6.3 — one node task as listed by <c>GET …/tasks?vmid=</c>.
/// <paramref name="Status"/> is the exit status (<c>OK</c> or an error string)
/// for finished tasks, <c>null</c> while still running.</summary>
public sealed record ProxmoxTask(
    string Upid,
    string Type,
    string? Status,
    string? User,
    long? StartTime,
    long? EndTime);

/// <summary>V6.4 — the live runtime snapshot from
/// <c>GET …/lxc/{vmid}/status/current</c>. Polled (Proxmox has no stats stream
/// for LXC) to drive the modal's real-time Stats view. <paramref name="NetIn"/>
/// / <paramref name="NetOut"/> are cumulative byte counters — the client turns
/// successive samples into rates.</summary>
public sealed record ProxmoxLxcStatus(
    string Status,
    double? Cpu,
    long? MemUsed,
    long? MemMax,
    long? DiskUsed,
    long? DiskMax,
    long? NetIn,
    long? NetOut,
    long? DiskRead,
    long? DiskWrite,
    long? UptimeSeconds);

/// <summary>V6.8 — the node's own health snapshot from
/// <c>GET /nodes/{node}/status</c>, normalised to bytes. Every field is
/// nullable so a partial response (older Proxmox, missing key) still renders a
/// useful card instead of a hard failure.</summary>
public sealed record ProxmoxNodeStatus(
    // ── CPU ──
    string? CpuModel,
    int? Sockets,
    int? Cpus,           // logical CPUs (threads) the node reports
    int? Cores,          // physical cores per socket, as Proxmox reports it
    double? CpuMhz,
    bool? Hvm,           // hardware virtualization (VT-x / AMD-V) available
    double? CpuFraction, // 0..1
    double? IoWaitFraction,
    double? Load1,
    double? Load5,
    double? Load15,
    // ── memory ──
    long? MemTotal,
    long? MemUsed,
    long? MemFree,
    long? SwapTotal,
    long? SwapUsed,
    // ── host root filesystem ──
    long? RootTotal,
    long? RootUsed,
    // ── lifecycle ──
    long? UptimeSeconds,
    string? KernelVersion,
    string? PveVersion,
    // ── subscription (best-effort second call) ──
    string? SubscriptionStatus,
    string? SubscriptionLevel);

/// <summary>V6.8 — one storage pool from <c>GET /nodes/{node}/storage</c>.</summary>
public sealed record ProxmoxNodeStorage(
    string Storage,
    string? Type,
    string? Content,
    bool Enabled,
    bool Active,
    long? Total,
    long? Used,
    long? Avail);

/// <summary>V6.8 — one physical disk + its SMART <em>summary</em> from
/// <c>GET /nodes/{node}/disks/list</c>. <paramref name="Health"/> is the SMART
/// overall-health string (e.g. <c>PASSED</c>); <paramref name="WearoutPercent"/>
/// is the SSD media-life-remaining percentage (null for spinning disks).</summary>
public sealed record ProxmoxNodeDisk(
    string DevPath,
    string? Model,
    string? Serial,
    string? Vendor,
    string? Type,        // hdd / ssd / nvme / usb …
    long? Size,
    string? Health,
    int? WearoutPercent,
    int? Rpm,
    string? Used);       // what the disk is used for (LVM / ZFS / partitions / "")

/// <summary>V6.8 — one critical SMART attribute from
/// <c>GET /nodes/{node}/disks/smart?disk=</c> (ATA disks). For NVMe Proxmox
/// returns free text instead, surfaced via <see cref="ProxmoxNodeDiskSmart.Text"/>.</summary>
public sealed record ProxmoxSmartAttribute(
    int Id,
    string Name,
    long? Value,
    long? Worst,
    long? Threshold,
    string? Raw);

/// <summary>V6.8 — detailed SMART for one disk, loaded on demand when a disk row
/// is expanded in the Storage/SMART tab.</summary>
public sealed record ProxmoxNodeDiskSmart(
    string? Health,
    string? Type,
    IReadOnlyList<ProxmoxSmartAttribute> Attributes,
    string? Text);

/// <summary>V6.8 — one network interface from <c>GET /nodes/{node}/network</c>.
/// This is the configured/link view (the Proxmox API has no per-interface
/// throughput); node-level RX/TX trend comes from the RRD series instead.</summary>
public sealed record ProxmoxNodeNetworkInterface(
    string Iface,
    string? Type,        // eth / bridge / bond / vlan …
    bool? Active,
    bool? Autostart,
    string? Method,
    string? Address,
    string? Cidr,
    string? Gateway,
    string? BridgePorts,
    string? BondSlaves);

/// <summary>V6.8 — one RRD sample for the node
/// (<c>GET /nodes/{node}/rrddata</c>). Rate fields are bytes/s averaged over the
/// bucket; <paramref name="Cpu"/> / <paramref name="IoWait"/> are 0..1.</summary>
public sealed record ProxmoxNodeRrdPoint(
    long Time,
    double? Cpu,
    double? IoWait,
    double? LoadAvg,
    long? MemTotal,
    long? MemUsed,
    long? NetIn,
    long? NetOut,
    long? RootTotal,
    long? RootUsed,
    long? SwapUsed);

/// <summary>
/// V6.0 — thin wrapper over the Proxmox VE REST API. Covers exactly the two
/// reads the update scan needs, both authenticated with a
/// <c>PVEAPIToken</c> header. Kept behind an interface so the orchestrator can
/// be tested without a live Proxmox host.
/// </summary>
public interface IProxmoxApiClient
{
    /// <summary>Lists the LXC containers on the node
    /// (<c>GET /nodes/{node}/lxc</c>).</summary>
    Task<IReadOnlyList<ProxmoxLxcSummary>> ListLxcAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — lists the QEMU/KVM virtual machines on the node
    /// (<c>GET /nodes/{node}/qemu</c>). Same resource fields as
    /// <see cref="ListLxcAsync"/> (they come free from the list response), so a VM
    /// card shows useful detail without any guest-agent. PBS has no VMs and
    /// returns an empty list.</summary>
    Task<IReadOnlyList<ProxmoxLxcSummary>> ListQemuAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Returns the number of pending package updates for the node
    /// itself (<c>GET /nodes/{node}/apt/update</c> returns one entry per
    /// upgradable package).</summary>
    Task<int> GetNodePendingUpdatesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Best-effort lookup of a running LXC's primary IPv4 address via
    /// <c>GET /nodes/{node}/lxc/{vmid}/interfaces</c> (works for LXC without a
    /// guest agent). Returns the first non-loopback IPv4, or <c>null</c> when it
    /// can't be determined — never throws.</summary>
    Task<string?> GetLxcIpAddressAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.2 — reads one LXC's full configuration plus its current
    /// runtime status, merged into a single <see cref="ProxmoxLxcDetail"/>.
    /// Backs the container modal's read-only Config tab.</summary>
    Task<ProxmoxLxcDetail> GetLxcDetailAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — reads one QEMU VM's configuration plus its current runtime
    /// status, merged into the same <see cref="ProxmoxLxcDetail"/> read model the
    /// LXC Config tab uses (VM config is <em>read-only</em> for now — its
    /// virtio/scsi/PCI-passthrough surface is a much larger structured-edit
    /// problem). The VM's disks map onto <see cref="ProxmoxLxcDetail.Mounts"/> and
    /// its NICs onto <see cref="ProxmoxLxcDetail.Networks"/>; the LXC-only fields
    /// (swap / arch / unprivileged / features) are left <c>null</c>.</summary>
    Task<ProxmoxLxcDetail> GetQemuDetailAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.3 — reads an LXC's RRD time-series for a timeframe
    /// (<c>hour</c>/<c>day</c>/<c>week</c>/<c>month</c>/<c>year</c>) for the
    /// Stats tab sparklines.</summary>
    Task<IReadOnlyList<ProxmoxLxcRrdPoint>> GetLxcRrdDataAsync(
        ProxmoxConnectionProfile profile, int vmId, string timeframe, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — a QEMU VM's RRD time-series
    /// (<c>GET …/qemu/{vmid}/rrddata</c>) for the Stats tab sparklines. Identical
    /// sample shape to the LXC series, so it reuses <see cref="ProxmoxLxcRrdPoint"/>
    /// and the same chart code.</summary>
    Task<IReadOnlyList<ProxmoxLxcRrdPoint>> GetQemuRrdDataAsync(
        ProxmoxConnectionProfile profile, int vmId, string timeframe, CancellationToken cancellationToken = default);

    /// <summary>V6.3 — recent node tasks scoped to one guest
    /// (<c>GET …/tasks?vmid=</c>), newest first, for the Tasks tab.</summary>
    Task<IReadOnlyList<ProxmoxTask>> GetLxcTasksAsync(
        ProxmoxConnectionProfile profile, int vmId, int limit, CancellationToken cancellationToken = default);

    /// <summary>V6.3 — reads one task's log (<c>GET …/tasks/{upid}/log</c>),
    /// joined into a single string, for the Tasks tab's per-task log viewer.</summary>
    Task<string> GetTaskLogAsync(
        ProxmoxConnectionProfile profile, string upid, int limit, CancellationToken cancellationToken = default);

    /// <summary>V6.4 — the LXC's current runtime status, polled for the
    /// real-time Stats view.</summary>
    Task<ProxmoxLxcStatus> GetLxcStatusAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — a QEMU VM's current runtime status, polled for the
    /// real-time Stats view (reuses <see cref="ProxmoxLxcStatus"/> — the
    /// <c>qemu/{vmid}/status/current</c> payload has the same fields).</summary>
    Task<ProxmoxLxcStatus> GetQemuStatusAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.4 — runs a lifecycle action on the LXC via
    /// <c>POST …/lxc/{vmid}/status/{action}</c>. <paramref name="action"/> must
    /// be <c>start</c> / <c>stop</c> / <c>shutdown</c> / <c>reboot</c>.</summary>
    Task LxcStatusActionAsync(
        ProxmoxConnectionProfile profile, int vmId, string action, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — runs a lifecycle action on a QEMU VM via
    /// <c>POST …/qemu/{vmid}/status/{action}</c>. Same verb set as the LXC path
    /// (<c>start</c> / <c>stop</c> / <c>shutdown</c> / <c>reboot</c>).</summary>
    Task QemuStatusActionAsync(
        ProxmoxConnectionProfile profile, int vmId, string action, CancellationToken cancellationToken = default);

    /// <summary>V6.13 — destroys an LXC via <c>DELETE …/lxc/{vmid}</c>. This is
    /// irreversible (the container's disk is removed). Needs the API token to hold
    /// <c>VM.Allocate</c> on the host. On a non-success status the Proxmox error
    /// body is surfaced as an <see cref="HttpRequestException"/> so the caller can
    /// show the host's own message (e.g. a refusal to destroy a running guest)
    /// verbatim.</summary>
    Task DeleteLxcAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.14 — destroys a QEMU VM via <c>DELETE …/qemu/{vmid}</c>. The VM
    /// analogue of <see cref="DeleteLxcAsync"/>: irreversible (the VM's disks are
    /// removed), needs <c>VM.Allocate</c>, and surfaces the host's error body
    /// verbatim on a non-success status.</summary>
    Task DeleteQemuAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.13.1 — creates an LXC via <c>POST /nodes/{node}/lxc</c> from a
    /// template, then polls the returned task UPID
    /// (<c>GET …/tasks/{upid}/status</c>) to a terminal state so the caller can
    /// report real success/failure rather than "request accepted". On a Proxmox
    /// rejection — either the initial POST or a non-<c>OK</c> task exit — the
    /// host's own message is surfaced as an <see cref="HttpRequestException"/> so
    /// the controller can relay it verbatim (e.g. a storage that can't hold a
    /// rootfs, or a vmid already in use). Needs the API token to hold
    /// <c>VM.Allocate</c> on the host.</summary>
    Task CreateLxcAsync(
        ProxmoxConnectionProfile profile, ProxmoxLxcCreate spec, CancellationToken cancellationToken = default);

    /// <summary>V6.13.1 — registers a freshly-created guest with the cluster's HA
    /// manager (<c>POST /cluster/ha/resources</c>, <c>sid=ct:{vmid}</c>). Called
    /// best-effort after a create when the user opted in; throws the host's message
    /// when HA isn't configured so the caller can relay it.</summary>
    Task AddHaResourceAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V6.13.1 — the next free cluster-wide guest id
    /// (<c>GET /cluster/nextid</c>), used to default the create form's vmid.</summary>
    Task<int> GetNextVmIdAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.13.1 — lists the available container templates across the
    /// node's template-capable storages (each storage whose content advertises
    /// <c>vztmpl</c>, then <c>GET …/storage/{storage}/content?content=vztmpl</c>),
    /// for the create form's template dropdown.</summary>
    Task<IReadOnlyList<ProxmoxTemplate>> ListTemplatesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V8.1 — lists the restorable LXC backup archives across the node's
    /// backup-capable storages (each storage whose content advertises <c>backup</c>,
    /// then <c>GET …/storage/{storage}/content?content=backup</c>), filtered to
    /// <c>vzdump-lxc-*</c> volumes, for the restore form's archive dropdown. Mirrors
    /// <see cref="ListTemplatesAsync"/>. PBS datastores (<c>pbs:</c> volumes) are out
    /// of scope and skipped.</summary>
    Task<IReadOnlyList<ProxmoxBackup>> ListBackupsAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V8.0 — clones an existing LXC via
    /// <c>POST /nodes/{node}/lxc/{vmid}/clone</c>, then polls the returned task
    /// UPID to a terminal state so the caller reports real success/failure (Proxmox
    /// clones asynchronously). A host rejection — the initial POST or a
    /// non-<c>OK</c> task exit — surfaces the host's own message as an
    /// <see cref="HttpRequestException"/> so the controller can relay it verbatim
    /// (e.g. a linked clone of a source without a snapshot). Needs the API token to
    /// hold <c>VM.Allocate</c> on the host.</summary>
    Task CloneLxcAsync(
        ProxmoxConnectionProfile profile, int sourceVmId, ProxmoxLxcClone spec, CancellationToken cancellationToken = default);

    /// <summary>V8.0 — lists an LXC's snapshots
    /// (<c>GET /nodes/{node}/lxc/{vmid}/snapshot</c>), newest first. The synthetic
    /// <c>current</c> pseudo-entry Proxmox always returns is filtered out so the
    /// result is purely real snapshots.</summary>
    Task<IReadOnlyList<ProxmoxSnapshot>> ListSnapshotsAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>V8.0 — takes a snapshot of an LXC
    /// (<c>POST /nodes/{node}/lxc/{vmid}/snapshot</c>) and polls the task to a
    /// terminal state. (Unlike a QEMU VM snapshot, an LXC snapshot has no
    /// running-memory option — the endpoint's schema rejects <c>vmstate</c>.) The
    /// host's message is surfaced verbatim on a rejection.</summary>
    Task CreateSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, string? description,
        CancellationToken cancellationToken = default);

    /// <summary>V8.0 — rolls an LXC back to a snapshot
    /// (<c>POST /nodes/{node}/lxc/{vmid}/snapshot/{name}/rollback</c>) and polls the
    /// task. This <em>discards</em> any state newer than the snapshot — the
    /// controller double-confirms before calling it. The host's message is surfaced
    /// verbatim on a rejection.</summary>
    Task RollbackSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default);

    /// <summary>V8.0 — deletes one LXC snapshot
    /// (<c>DELETE /nodes/{node}/lxc/{vmid}/snapshot/{name}</c>) and polls the
    /// returned task to a terminal state. The host's message is surfaced verbatim on
    /// a rejection.</summary>
    Task DeleteSnapshotAsync(
        ProxmoxConnectionProfile profile, int vmId, string name, CancellationToken cancellationToken = default);

    /// <summary>V6.5 / V6.9 — writes the editable subset of an LXC's config via
    /// <c>PUT …/lxc/{vmid}/config</c>. Only the non-null scalar fields and the
    /// supplied network / mount / rootfs operations on <paramref name="update"/>
    /// are sent (Proxmox merges them into the existing config; untouched keys stay
    /// as-is). Adds (a change with an empty key) are assigned the next free
    /// <c>net&lt;n&gt;</c> / <c>mp&lt;n&gt;</c> after reading the current config,
    /// and removes are emitted as a single <c>delete=</c> list. Needs the API
    /// token to hold <c>VM.Config.*</c> on the host. When nothing on
    /// <paramref name="update"/> is set the call is a no-op.</summary>
    Task UpdateLxcConfigAsync(
        ProxmoxConnectionProfile profile, int vmId, ProxmoxLxcConfigUpdate update, CancellationToken cancellationToken = default);

    // ── V6.8 — PVE node card ─────────────────────────────────────────────────

    /// <summary>V6.8 — the node's own health snapshot
    /// (<c>GET /nodes/{node}/status</c>), enriched best-effort with the
    /// subscription state (<c>GET /nodes/{node}/subscription</c>). Backs the node
    /// card summary and the modal's Overview / CPU+RAM tabs.</summary>
    Task<ProxmoxNodeStatus> GetNodeStatusAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — the node's RRD time-series for a timeframe, for the
    /// CPU/RAM and Network sparklines.</summary>
    Task<IReadOnlyList<ProxmoxNodeRrdPoint>> GetNodeRrdDataAsync(
        ProxmoxConnectionProfile profile, string timeframe, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — per-storage-pool usage (<c>GET /nodes/{node}/storage</c>).</summary>
    Task<IReadOnlyList<ProxmoxNodeStorage>> GetNodeStoragesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — physical disks with their SMART health summary + wearout
    /// (<c>GET /nodes/{node}/disks/list</c>).</summary>
    Task<IReadOnlyList<ProxmoxNodeDisk>> GetNodeDisksAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — detailed SMART for one disk
    /// (<c>GET /nodes/{node}/disks/smart?disk=</c>), loaded on demand.</summary>
    Task<ProxmoxNodeDiskSmart> GetNodeDiskSmartAsync(
        ProxmoxConnectionProfile profile, string devPath, CancellationToken cancellationToken = default);

    /// <summary>V6.8 — configured network interfaces
    /// (<c>GET /nodes/{node}/network</c>).</summary>
    Task<IReadOnlyList<ProxmoxNodeNetworkInterface>> GetNodeNetworkInterfacesAsync(
        ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);
}
