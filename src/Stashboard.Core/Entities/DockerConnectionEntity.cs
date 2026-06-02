using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// User-scoped Docker daemon connection. Each user can configure many
/// connections (e.g. <c>home-server</c>, <c>vps-prod</c>) and assign each
/// <see cref="WebResourceEntity"/> to one of them — a single host can back
/// many services, so connections are shared rather than per-service.
/// </summary>
public class DockerConnectionEntity : AuditableEntity
{
    /// <summary>Owning user. Unique together with <see cref="Name"/> so the
    /// dropdown in the UI has stable, recognisable labels.</summary>
    public Guid UserId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    public DockerHostType HostType { get; set; } = DockerHostType.LocalSocket;

    [MaxLength(500)]
    public string? HostUrl { get; set; }

    /// <summary>Encrypted TLS CA certificate (PEM) for <see cref="DockerHostType.TcpTls"/> hosts.</summary>
    public string? TlsCaCertEncrypted { get; set; }
    public string? TlsClientCertEncrypted { get; set; }
    public string? TlsClientKeyEncrypted { get; set; }

    /// <summary>
    /// V2.5 — SSH host (DNS name or IP) for <see cref="DockerHostType.Ssh"/>
    /// connections. Stored plaintext; not a secret.
    /// </summary>
    [MaxLength(200)]
    public string? SshHost { get; set; }

    /// <summary>V2.5 — SSH port (default 22).</summary>
    public int? SshPort { get; set; }

    /// <summary>V2.5 — SSH login username (plaintext, not a secret).</summary>
    [MaxLength(100)]
    public string? SshUsername { get; set; }

    /// <summary>
    /// V2.5 — PEM-encoded OpenSSH private key for authenticating against the
    /// remote host. Stored encrypted via the same <c>IEncryptionService</c>
    /// the TLS material uses. Passphrase-protected keys are supported by
    /// supplying the passphrase separately in <see cref="SshPrivateKeyPassphraseEncrypted"/>.
    /// </summary>
    public string? SshPrivateKeyEncrypted { get; set; }

    /// <summary>
    /// V2.5 — optional passphrase for <see cref="SshPrivateKeyEncrypted"/>.
    /// Only meaningful when the private key itself is passphrase-protected.
    /// </summary>
    public string? SshPrivateKeyPassphraseEncrypted { get; set; }

    /// <summary>
    /// V2.5 — path of the Docker socket on the remote host. Defaults to
    /// <c>/var/run/docker.sock</c>; can be overridden for rootless Docker
    /// setups (<c>/run/user/1000/docker.sock</c>) or non-standard install
    /// locations.
    /// </summary>
    [MaxLength(200)]
    public string? SshRemoteSocketPath { get; set; }

    /// <summary>
    /// V5.2 — absolute path, as visible from inside the Stashboard container,
    /// to the directory that holds this host's <c>docker-compose.yml</c>. The
    /// operator bind-mounts the host's Compose project directory to this path
    /// (e.g. <c>/compose-projects/home-server</c>) so the <c>docker compose</c>
    /// CLI inside the container can resolve <c>env_file</c> paths,
    /// <c>depends_on</c> ordering and profiles when "Update now" recreates a
    /// container. <c>null</c> keeps the V2.7 raw <c>Docker.DotNet</c> recreate.
    /// Only honoured for <see cref="DockerHostType.LocalSocket"/> connections.
    /// </summary>
    [MaxLength(500)]
    public string? ComposeProjectPath { get; set; }

    /// <summary>
    /// V5.3 — opt-in switch for the browser host terminal (an interactive SSH
    /// shell on the Docker host). Off by default: this is the most dangerous
    /// surface in the product (host-level RCE), so it has to be turned on
    /// deliberately per connection, and only ever does anything for
    /// <see cref="DockerHostType.Ssh"/> hosts (a <c>LocalSocket</c> shell would
    /// land on Stashboard's own container and <c>TcpTls</c> exposes no shell).
    /// The server also requires the global <c>Stashboard:AllowHostShell</c>
    /// flag before honouring it.
    /// </summary>
    public bool AllowHostShell { get; set; }

    /// <summary>
    /// V5.7 — opt-in switch for the browser container-exec terminal (an
    /// interactive shell <em>inside</em> a container via the Docker daemon's
    /// <c>exec</c> API). Off by default: exec runs arbitrary commands in the
    /// workload, so it has to be turned on deliberately per connection. Unlike
    /// <see cref="AllowHostShell"/> (SSH-only, lands on the host), exec works
    /// for every host type because it goes through the daemon rather than an
    /// SSH login. The server also requires the global
    /// <c>Stashboard:AllowContainerExec</c> switch before honouring it.
    /// </summary>
    public bool AllowExec { get; set; }

    /// <summary>
    /// V5.5 — per-connection opt-out for the background image-prune sweep.
    /// Default <c>true</c>: pruning dangling images is safe and is the whole
    /// point of the feature, so any newly added connection participates
    /// unless the operator turns it off here.
    /// </summary>
    public bool AllowImagePrune { get; set; } = true;

    /// <summary>
    /// V5.5 — per-connection opt-in to also prune <em>unused</em> images
    /// (anything not referenced by a running or stopped container). Off by
    /// default — more aggressive, and can break "rollback to previous tag"
    /// workflows by removing the previous version's image entirely.
    /// </summary>
    public bool PruneUnusedImages { get; set; }

    /// <summary>V5.5 — UTC timestamp of the last successful prune run.
    /// Used by the background sweep to space runs out per the configured
    /// interval, and surfaced on the storage widget.</summary>
    public DateTime? LastImagePruneUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
