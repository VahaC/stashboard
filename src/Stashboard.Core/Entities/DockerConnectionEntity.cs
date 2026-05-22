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

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
