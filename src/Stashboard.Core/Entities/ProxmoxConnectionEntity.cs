using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.0 — a user-scoped Proxmox VE host that Stashboard polls for pending
/// package updates. A single host carries two transports because neither
/// alone covers the whole job:
/// <list type="bullet">
///   <item>The <strong>REST API</strong> (API token) lists LXC containers
///   (<c>GET /nodes/{node}/lxc</c>) and reports the node's own pending
///   updates (<c>GET /nodes/{node}/apt/update</c>).</item>
///   <item><strong>SSH</strong> runs <c>pct exec &lt;vmid&gt; -- apt list
///   --upgradable</c> for the per-LXC update count — the Proxmox API exposes
///   no command-exec endpoint for LXC, so this is the only way to read it.</item>
/// </list>
/// Discovered guests are persisted as child <see cref="ProxmoxGuestEntity"/>
/// rows, refreshed on every scan. Schedule + notification fields mirror
/// <see cref="DockerWatchEntity"/> so the V2.2 <c>CheckScheduleEvaluator</c>
/// and the existing email/Telegram channels are reused unchanged.
/// </summary>
public class ProxmoxConnectionEntity : AuditableEntity
{
    /// <summary>Owning user. Unique together with <see cref="Name"/> so the
    /// list has stable, recognisable labels.</summary>
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    /// <summary>Base URL of the Proxmox API, e.g. <c>https://pve.lan:8006</c>.
    /// The <c>/api2/json</c> path is appended by the client. Plaintext — not a
    /// secret.</summary>
    [Required, MaxLength(300)]
    public string ApiBaseUrl { get; set; } = default!;

    /// <summary>Node name as it appears in Proxmox (e.g. <c>pve</c>). Used to
    /// build the <c>/nodes/{node}/…</c> path. Plaintext.</summary>
    [Required, MaxLength(100)]
    public string NodeName { get; set; } = default!;

    /// <summary>V6.8.3 — whether this host is Proxmox VE or Proxmox Backup
    /// Server. Drives the API-token auth header and the node/storage endpoints
    /// the client uses. Defaults to <see cref="ProxmoxServerType.Pve"/> so the
    /// additive migration leaves every existing host as PVE.</summary>
    public ProxmoxServerType ServerType { get; set; } = ProxmoxServerType.Pve;

    /// <summary>API token id in Proxmox's <c>USER@REALM!TOKENID</c> form
    /// (e.g. <c>root@pam!stashboard</c>). Plaintext — it's an identifier, the
    /// secret half lives in <see cref="ApiTokenSecretEncrypted"/>.</summary>
    [Required, MaxLength(200)]
    public string ApiTokenId { get; set; } = default!;

    /// <summary>Encrypted API token secret (the UUID half of
    /// <c>PVEAPIToken=USER@REALM!TOKENID=SECRET</c>).</summary>
    public string? ApiTokenSecretEncrypted { get; set; }

    /// <summary>Skip TLS certificate validation when calling the API. Homelab
    /// Proxmox installs almost always use a self-signed certificate, so this
    /// defaults to <c>true</c> — but it is exposed so a user with a proper
    /// certificate can tighten it.</summary>
    public bool SkipTlsVerify { get; set; } = true;

    // ── SSH transport (per-LXC apt counts) ─────────────────────────────────

    /// <summary>SSH host (DNS name or IP). Often the same machine as the API,
    /// but kept separate so a user can route SSH differently. Plaintext.</summary>
    [MaxLength(200)]
    public string? SshHost { get; set; }

    /// <summary>SSH port (default 22).</summary>
    public int? SshPort { get; set; }

    /// <summary>SSH login username (e.g. <c>root</c>). <c>pct</c> requires
    /// root or a sudo-capable user. Plaintext.</summary>
    [MaxLength(100)]
    public string? SshUsername { get; set; }

    /// <summary>PEM-encoded OpenSSH private key, encrypted via the same
    /// <c>IEncryptionService</c> the Docker SSH path uses.</summary>
    public string? SshPrivateKeyEncrypted { get; set; }

    /// <summary>Optional passphrase for <see cref="SshPrivateKeyEncrypted"/>.</summary>
    public string? SshPrivateKeyPassphraseEncrypted { get; set; }

    /// <summary>V6.6 — per-host opt-in to the browser LXC console (an
    /// interactive shell inside a guest via <c>pct exec</c> over SSH). Off by
    /// default; the server also requires the global
    /// <c>Stashboard:AllowProxmoxConsole</c> switch and configured SSH
    /// credentials before honouring it. Mirrors
    /// <c>DockerConnection.AllowExec</c>.</summary>
    public bool AllowConsole { get; set; }

    /// <summary>V6.7.1 — per-host opt-in to one-click <strong>Update now</strong>
    /// (apply pending package updates by running <c>apt-get dist-upgrade</c> on
    /// the node and inside its LXCs over SSH). Off by default; the server also
    /// requires the global <c>Stashboard:AllowProxmoxUpdates</c> switch and
    /// configured SSH credentials before honouring it. Mirrors
    /// <see cref="AllowConsole"/> — applying updates runs arbitrary privileged
    /// package operations, so it is gated exactly like the console.</summary>
    public bool AllowUpdates { get; set; }

    /// <summary>V6.13 — per-host opt-in to <strong>destroying</strong> an LXC
    /// (<c>DELETE /nodes/{node}/lxc/{vmid}</c>). Off by default; the server also
    /// requires the global <c>Stashboard:AllowProxmoxDestroy</c> switch, the guest
    /// to be stopped, and an explicit double confirmation in the UI before
    /// honouring it. Mirrors <see cref="AllowConsole"/> / <see cref="AllowUpdates"/>
    /// — destroying a container is irreversible, so it is gated exactly like the
    /// console and update-apply actions. Needs the API token to hold
    /// <c>VM.Allocate</c> on the host.</summary>
    public bool AllowDestroy { get; set; }

    /// <summary>V6.13.1 — per-host opt-in to <strong>creating</strong> an LXC
    /// (<c>POST /nodes/{node}/lxc</c> from a template). Off by default; the server
    /// also requires the global <c>Stashboard:AllowProxmoxCreate</c> switch before
    /// honouring it. Mirrors <see cref="AllowDestroy"/> — creating a container
    /// provisions real storage / network on the host, so it is gated like the
    /// other lifecycle write actions. Needs the API token to hold
    /// <c>VM.Allocate</c> on the host.</summary>
    public bool AllowCreate { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>V6.8.2 — how often (seconds) the node modal's live telemetry tabs
    /// poll this host (status card, RRD, sensors, and the SSH deep-telemetry
    /// collectors). <c>null</c> means use the app default (20s). Clamped to
    /// 5..300 on write. The real-time 2s "Live" view is unaffected.</summary>
    public int? TelemetryPollSeconds { get; set; }

    public bool UpdateNotificationsEnabled { get; set; } = true;

    /// <summary>Send a Telegram message in addition to email when new pending
    /// updates are first detected. Effective only when the owner has configured
    /// Telegram on the Account page.</summary>
    public bool TelegramNotificationsEnabled { get; set; } = false;

    /// <summary>V2.2 schedule mode, reused verbatim. Default Hourly / 24 h.</summary>
    public CheckScheduleType ScheduleType { get; set; } = CheckScheduleType.Hourly;

    /// <summary>Hourly cadence. Allowed values: 1, 2, 4, 6, 12, 24. Default 24.</summary>
    public int CheckEveryHours { get; set; } = 24;

    /// <summary>Daily / Weekly target time (UTC).</summary>
    public TimeOnly? CheckAtTime { get; set; }

    /// <summary>Weekly target day.</summary>
    public DayOfWeek? CheckOnDayOfWeek { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Connection-level error from the most recent scan (API/SSH
    /// unreachable, auth failure). <c>null</c> on success. Per-guest errors
    /// live on the guest rows.</summary>
    public string? LastError { get; set; }

    /// <summary>UTC timestamp of the most recent update notification.</summary>
    public DateTime? LastNotificationSentUtc { get; set; }

    /// <summary>Signature of the pending-update state already emailed about —
    /// a stable digest of "(vmid:count)" pairs. Re-notification fires only when
    /// the signature changes, so the user isn't pinged every tick while the
    /// same updates sit pending. Stamped only after a successful send.</summary>
    [MaxLength(200)]
    public string? LastNotifiedSignature { get; set; }

    /// <summary>Same as <see cref="LastNotifiedSignature"/> for the Telegram
    /// channel — independent throttle so a transient failure on one channel
    /// doesn't suppress the other.</summary>
    [MaxLength(200)]
    public string? LastTelegramNotifiedSignature { get; set; }

    /// <summary>
    /// V6.11 — hex-encoded 32-byte random token authenticating an inbound update-
    /// check webhook for this host (the Proxmox analogue of
    /// <see cref="DockerWatchEntity.WebhookToken"/>). <c>null</c> means the user
    /// hasn't opted in to webhook delivery (the default — it's an external trigger
    /// surface, so it stays off until explicitly created). When set, the public
    /// endpoint <c>POST /api/proxmox/webhooks/{token}</c> enqueues an out-of-band
    /// scan of this host, bypassing the schedule. Rotating the token in the UI
    /// invalidates the old URL immediately.
    /// </summary>
    [MaxLength(64)]
    public string? WebhookToken { get; set; }

    /// <summary>V6.11 — UTC timestamp of the most recent successfully-accepted
    /// webhook delivery, surfaced in the UI so the user can verify their trigger
    /// is actually firing. <c>null</c> until the first hit lands.</summary>
    public DateTime? LastWebhookReceivedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
