using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

/// <summary>
/// V6.0 — read model for a Proxmox host. Never carries decrypted secrets —
/// only presence flags (<see cref="HasApiTokenSecret"/>,
/// <see cref="HasSshPrivateKey"/>) so the UI can render "configured / not
/// configured" without leaking values. <see cref="Guests"/> holds the
/// auto-discovered node + LXC cards from the last scan.
/// </summary>
public sealed record ProxmoxConnectionResponse(
    Guid Id,
    string Name,
    string ApiBaseUrl,
    string NodeName,
    /// <summary>V6.8.3 — PVE or PBS.</summary>
    ProxmoxServerType ServerType,
    string ApiTokenId,
    bool HasApiTokenSecret,
    bool SkipTlsVerify,
    string? SshHost,
    int? SshPort,
    string? SshUsername,
    bool HasSshPrivateKey,
    bool HasSshPrivateKeyPassphrase,
    /// <summary>V6.6 — per-host opt-in to the browser LXC console.</summary>
    bool AllowConsole,
    /// <summary>V6.7.1 — per-host opt-in to one-click "Update now".</summary>
    bool AllowUpdates,
    /// <summary>V6.13 — per-host opt-in to destroying an LXC.</summary>
    bool AllowDestroy,
    /// <summary>V6.13.1 — per-host opt-in to creating an LXC.</summary>
    bool AllowCreate,
    bool Enabled,
    bool UpdateNotificationsEnabled,
    bool TelegramNotificationsEnabled,
    CheckScheduleType scheduleType,
    int CheckEveryHours,
    TimeOnly? CheckAtTime,
    DayOfWeek? CheckOnDayOfWeek,
    DateTime? LastCheckedUtc,
    string? LastError,
    IReadOnlyList<ProxmoxGuestResponse> Guests,
    /// <summary>V6.8.2 — node-modal telemetry poll interval (seconds); <c>null</c>
    /// means the app default (20s).</summary>
    int? TelemetryPollSeconds,
    /// <summary>V6.11 — the host's public update-check webhook token, or
    /// <c>null</c> when webhook delivery isn't enabled. The full URL is
    /// <c>POST /api/proxmox/webhooks/{token}</c>.</summary>
    string? WebhookToken,
    /// <summary>V6.11 — when the last webhook delivery was accepted, or
    /// <c>null</c>.</summary>
    DateTime? LastWebhookReceivedUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>V6.0 — one discovered node/LXC card.</summary>
public sealed record ProxmoxGuestResponse(
    int VmId,
    string Name,
    ProxmoxGuestType GuestType,
    bool IsRunning,
    int? PendingUpdates,
    string? LastError,
    string? IpAddress,
    long? UptimeSeconds,
    int? CpuCores,
    long? MemoryBytes,
    long? DiskBytes,
    /// <summary>Proxmox tags split from the raw semicolon-separated string.</summary>
    IReadOnlyList<string> Tags,
    DateTime? LastCheckedUtc,
    /// <summary>V6.7 — whether update monitoring is on for this LXC. Always
    /// <c>true</c> for the node row.</summary>
    bool MonitoringEnabled,
    /// <summary>V6.11 — active maintenance-window snooze until this UTC instant,
    /// or <c>null</c> when not snoozed. While set the guest is skipped by checks
    /// exactly like monitoring-off, then auto-re-included once it passes.</summary>
    DateTime? MonitoringSnoozedUntil,
    /// <summary>V7.9 — the service this guest is linked to, or <c>null</c> when
    /// standalone. Mirrors the Docker watch's optional single service link: the
    /// modal renders a "Linked service" dropdown, and the linked service shows a
    /// Proxmox update badge on its dashboard card.</summary>
    Guid? LinkedServiceId = null,
    string? LinkedServiceName = null);

/// <summary>V6.7 — toggle a single LXC's update monitoring on/off (the Proxmox
/// analogue of enabling/disabling a Docker watch).</summary>
public sealed record ProxmoxGuestMonitoringRequest(bool Enabled);

/// <summary>V7.9 — link a guest to a single service (or unlink with
/// <c>null</c>), the Proxmox analogue of a Docker watch's "Linked service"
/// dropdown. Replaces any existing service link for this guest.</summary>
public sealed record ProxmoxGuestServiceLinkRequest(Guid? WebResourceId);

/// <summary>V6.11 — host-wide bulk monitoring toggle: enable or disable update
/// monitoring for every LXC on the host in one call. The node row is never
/// affected (it's host-controlled).</summary>
public sealed record ProxmoxBulkMonitoringRequest(bool Enabled);

/// <summary>V6.11 — snooze one LXC's update monitoring for a maintenance window.
/// <see cref="Until"/> is the UTC instant to skip checks until; <c>null</c>
/// clears an active snooze (re-enabling checks immediately). Must be in the
/// future when set.</summary>
public sealed record ProxmoxGuestSnoozeRequest(DateTime? Until);

/// <summary>V6.11 — apply pending updates across several LXCs on a host in one
/// streaming run. <see cref="VmIds"/> is the set of LXC vmids to update in turn;
/// the node is never included (use the node "Update now"). Each guest gets its
/// own audit session and its apt log streams in sequence.</summary>
public sealed record ProxmoxBulkUpdateRequest(IReadOnlyList<int> VmIds);

/// <summary>
/// V6.7.1 — app-wide one-click "Update now" master switch, managed from the
/// Settings page. This is the global gate; a per-host opt-in
/// (<c>AllowUpdates</c>) and SSH credentials are also required before updates
/// can be applied.
/// </summary>
public sealed record ProxmoxUpdateApplySettingsResponse(bool Enabled);

/// <summary>V6.7.1 — update payload for the "Update now" master switch.</summary>
public sealed record UpdateProxmoxUpdateApplySettingsRequest(bool Enabled);

/// <summary>
/// V6.13 — app-wide destroy-LXC master switch, managed from the Settings page.
/// This is the global gate; a per-host opt-in (<c>AllowDestroy</c>), a stopped
/// guest, and a UI double confirmation are also required before a container can
/// be destroyed.
/// </summary>
public sealed record ProxmoxDestroySettingsResponse(bool Enabled);

/// <summary>V6.13 — update payload for the destroy-LXC master switch.</summary>
public sealed record UpdateProxmoxDestroySettingsRequest(bool Enabled);

/// <summary>
/// V6.13.1 — app-wide create-LXC master switch, managed from the Settings page.
/// This is the global gate; a per-host opt-in (<c>AllowCreate</c>) is also
/// required before a container can be created.
/// </summary>
public sealed record ProxmoxCreateSettingsResponse(bool Enabled);

/// <summary>V6.13.1 — update payload for the create-LXC master switch.</summary>
public sealed record UpdateProxmoxCreateSettingsRequest(bool Enabled);

/// <summary>V6.13.1 — the next free guest id from <c>GET /cluster/nextid</c>, for
/// the create form's vmid default.</summary>
public sealed record ProxmoxNextIdResponse(int VmId);

/// <summary>
/// V6.13.1 — create one LXC from a template (<c>POST /nodes/{node}/lxc</c>). The
/// scalar fields mirror <see cref="Stashboard.Core.Abstractions.ProxmoxLxcCreate"/>;
/// <see cref="Net0"/> reuses the V6.9 structured network model so the create
/// form and the edit form format an interface identically. The password / SSH
/// key are write-only — sent to Proxmox, never returned. Locally-checkable guards
/// (vmid range, valid CIDR/MAC, positive sizes) are validated up front; Proxmox
/// stays authoritative for everything that needs the host (storage / template
/// existence, vmid collisions it owns).
/// </summary>
public sealed record ProxmoxLxcCreateRequest(
    [Range(100, 999_999_999)] int VmId,
    [Required, MaxLength(512)] string OsTemplate,
    [Required, MaxLength(100)] string RootfsStorage,
    [Range(1, 131072)] int RootfsSizeGib,
    [MaxLength(255)] string? Hostname = null,
    [MaxLength(8192)] string? Description = null,
    [MaxLength(1024)] string? Tags = null,
    [MaxLength(512)] string? Password = null,
    [MaxLength(8192)] string? SshPublicKeys = null,
    [Range(1, 8192)] int? Cores = null,
    [Range(16, 4194304)] long? MemoryMib = null,
    [Range(0, 4194304)] long? SwapMib = null,
    ProxmoxLxcNetChange? Net0 = null,
    bool Unprivileged = true,
    bool Onboot = false,
    bool Start = false,
    bool Nesting = false,
    [MaxLength(255)] string? Nameserver = null,
    [MaxLength(255)] string? SearchDomain = null,
    bool AddToHa = false);

/// <summary>V6.7.1 — the exact remote command a one-click "Update now" will run,
/// surfaced so the UI can show + let the operator copy it before confirming
/// (the Proxmox analogue of the Docker "Update command" panel).</summary>
public sealed record ProxmoxUpdateCommandResponse(string Command);

/// <summary>
/// V6.0 — create-or-update payload. The API token secret, SSH private key, and
/// passphrase honour the tri-state <see cref="SecretValueUpsert"/> semantics so
/// an edit can preserve the persisted encrypted value without round-tripping it.
/// </summary>
public sealed record ProxmoxConnectionUpsertRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(300)] string ApiBaseUrl,
    [Required, MaxLength(100)] string NodeName,
    [Required, MaxLength(200)] string ApiTokenId,
    SecretValueUpsert? ApiTokenSecret,
    ProxmoxServerType ServerType = ProxmoxServerType.Pve,
    bool SkipTlsVerify = true,
    [MaxLength(200)] string? SshHost = null,
    int? SshPort = null,
    [MaxLength(100)] string? SshUsername = null,
    SecretValueUpsert? SshPrivateKey = null,
    SecretValueUpsert? SshPrivateKeyPassphrase = null,
    bool AllowConsole = false,
    bool AllowUpdates = false,
    bool AllowDestroy = false,
    bool AllowCreate = false,
    bool Enabled = true,
    bool UpdateNotificationsEnabled = true,
    bool TelegramNotificationsEnabled = false,
    CheckScheduleType ScheduleType = CheckScheduleType.Hourly,
    int CheckEveryHours = 24,
    TimeOnly? CheckAtTime = null,
    DayOfWeek? CheckOnDayOfWeek = null,
    /// <summary>V6.8.2 — node-modal telemetry poll interval (seconds), clamped to
    /// 5..300; <c>null</c> resets to the app default (20s).</summary>
    int? TelemetryPollSeconds = null);

/// <summary>
/// V6.0 — test-connection request. Same shape as the upsert minus the fields
/// that don't affect reachability; never persisted. Secret fields follow the
/// tri-state convention so an existing host can be tested after a host-URL
/// edit without re-entering the token / key.
/// </summary>
public sealed record ProxmoxConnectionPingRequest(
    [Required, MaxLength(300)] string ApiBaseUrl,
    [Required, MaxLength(100)] string NodeName,
    [Required, MaxLength(200)] string ApiTokenId,
    SecretValueUpsert? ApiTokenSecret,
    ProxmoxServerType ServerType = ProxmoxServerType.Pve,
    bool SkipTlsVerify = true,
    [MaxLength(200)] string? SshHost = null,
    int? SshPort = null,
    [MaxLength(100)] string? SshUsername = null,
    SecretValueUpsert? SshPrivateKey = null,
    SecretValueUpsert? SshPrivateKeyPassphrase = null);

/// <summary>V6.0 — outcome of a connection test. <see cref="SshReachable"/> is
/// <c>null</c> when SSH wasn't configured (and therefore not tested).</summary>
public sealed record ProxmoxConnectionPingResponse(
    bool ApiReachable,
    bool? SshReachable,
    string? Error);

// ── V6.8.1 — PVE node alerting ──────────────────────────────────────────────

/// <summary>V6.8.1 — which deviation categories are armed for a node, as plain
/// booleans (the API surface for the persisted <c>ProxmoxAlertCategory</c>
/// mask). Defaults to all-on when a node is first enabled.</summary>
public sealed record ProxmoxAlertCategoryToggles(
    bool Cpu,
    bool Memory,
    bool Storage,
    bool Thermal,
    bool Smart,
    bool Network);

/// <summary>V6.8.1 — a warn/crit threshold set. On the response's
/// <c>Thresholds</c> a <c>null</c> means "use the global default"; on
/// <c>Defaults</c> every field is populated so the UI can show the baseline as a
/// placeholder. The same shape is reused as the upsert payload.</summary>
public sealed record ProxmoxNodeAlertThresholdValues(
    int? CpuWarn,
    int? CpuCrit,
    int? MemWarn,
    int? MemCrit,
    int? StorageWarn,
    int? StorageCrit,
    int? TempWarn,
    int? TempCrit);

/// <summary>V6.8.1 — a node's alert configuration. <see cref="Thresholds"/> holds
/// the per-node overrides (nullable); <see cref="Defaults"/> echoes the global
/// baseline so the form can render placeholders for un-overridden fields.</summary>
public sealed record ProxmoxNodeAlertSettingsResponse(
    bool Enabled,
    ProxmoxAlertCategoryToggles Categories,
    ProxmoxNodeAlertThresholdValues Thresholds,
    ProxmoxNodeAlertThresholdValues Defaults,
    DateTime? LastNotificationSentUtc);

/// <summary>V6.8.1 — upsert payload for a node's alert configuration. Threshold
/// overrides are validated 1..100 (CPU/RAM/storage %) and 1..150 (°C); a
/// warn/crit pair where warn ≥ crit is rejected by the controller.</summary>
public sealed record ProxmoxNodeAlertSettingsUpdateRequest(
    bool Enabled,
    ProxmoxAlertCategoryToggles Categories,
    ProxmoxNodeAlertThresholdValues Thresholds);

/// <summary>V6.8.1 — one currently-active alert for the node modal's Alerts tab.
/// <see cref="Severity"/> is <c>"warn"</c> / <c>"crit"</c> and
/// <see cref="Category"/> is the lower-case category name.</summary>
public sealed record ProxmoxNodeAlertResponse(
    string Category,
    string Severity,
    string? Metric,
    double? Value,
    double? Threshold,
    DateTime? FirstSeenUtc);

/// <summary>
/// V6.5 / V6.9 — edit one LXC's config. The scalar subset (V6.5) is optional
/// per-field: a <c>null</c> leaves that key untouched, so the UI sends only what
/// the user changed. Memory / swap are in MiB to match Proxmox's native config
/// unit. V6.9 adds structured <see cref="Networks"/> / <see cref="Mounts"/> /
/// <see cref="Rootfs"/> edits, modelled as intentful add/update/remove
/// operations (a <c>null</c> list means "no changes here"). Written via
/// <c>PUT /nodes/{node}/lxc/{vmid}/config</c>, which needs the API token to hold
/// <c>VM.Config.*</c>.
/// </summary>
public sealed record ProxmoxLxcConfigUpdateRequest(
    [Range(1, 8192)] int? Cores = null,
    [Range(16, 4194304)] long? MemoryMib = null,
    [Range(0, 4194304)] long? SwapMib = null,
    [MaxLength(255)] string? Hostname = null,
    bool? Onboot = null,
    IReadOnlyList<ProxmoxLxcNetChange>? Networks = null,
    IReadOnlyList<ProxmoxLxcMountChange>? Mounts = null,
    ProxmoxLxcRootfsChange? Rootfs = null);
