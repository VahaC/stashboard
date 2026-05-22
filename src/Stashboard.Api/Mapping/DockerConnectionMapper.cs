using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Mapping;

/// <summary>
/// Maps between <see cref="DockerConnectionEntity"/> and its API contracts.
/// Owns the encryption / tri-state-secret semantics for the TLS and SSH
/// material so the controller can stay focused on transport.
/// </summary>
/// <remarks>
/// V2.5 — produces a single <see cref="DockerHostTransport"/> bundle covering
/// every host type (local socket, TCP+TLS, SSH) so the host client signature
/// stops growing for each new transport. SSH secrets follow the same tri-state
/// <see cref="SecretValueAction"/> convention as TLS.
/// </remarks>
public interface IDockerConnectionMapper
{
    DockerConnectionResponse ToResponse(DockerConnectionEntity entity, int usageCount);

    /// <summary>Applies the upsert payload to <paramref name="entity"/> in place.</summary>
    void ApplyUpsert(DockerConnectionEntity entity, DockerConnectionUpsertRequest request);

    /// <summary>
    /// Builds the decrypted transport bundle for transient host calls (ping,
    /// list containers, generate update command) without persisting anything.
    /// Resolves tri-state secrets against the persisted connection when one exists.
    /// </summary>
    DockerHostTransport BuildTransport(
        DockerConnectionPingRequest request,
        DockerConnectionEntity? existing);

    /// <summary>Decrypts the persisted entity's transport material for host calls.</summary>
    DockerHostTransport BuildTransport(DockerConnectionEntity entity);
}

public sealed class DockerConnectionMapper(IEncryptionService encryption) : IDockerConnectionMapper
{
    /// <summary>Default remote socket path used when the connection doesn't
    /// override it — matches the rootful Docker daemon's well-known location.</summary>
    public const string DefaultRemoteSocketPath = "/var/run/docker.sock";

    public DockerConnectionResponse ToResponse(DockerConnectionEntity entity, int usageCount) =>
        new(
            entity.Id,
            entity.Name,
            entity.HostType,
            entity.HostUrl,
            HasTlsConfigured: HasAnyTls(entity),
            // V2.5 — surface presence flags for the SSH material so the UI can
            // render "configured / not configured" without leaking values.
            SshHost: entity.SshHost,
            SshPort: entity.SshPort,
            SshUsername: entity.SshUsername,
            HasSshPrivateKey: !string.IsNullOrEmpty(entity.SshPrivateKeyEncrypted),
            HasSshPrivateKeyPassphrase: !string.IsNullOrEmpty(entity.SshPrivateKeyPassphraseEncrypted),
            SshRemoteSocketPath: entity.SshRemoteSocketPath,
            UsageCount: usageCount,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    public void ApplyUpsert(DockerConnectionEntity entity, DockerConnectionUpsertRequest request)
    {
        entity.Name = request.Name.Trim();
        entity.HostType = request.HostType;
        entity.HostUrl = request.HostType == DockerHostType.TcpTls ? request.HostUrl : null;
        entity.TlsCaCertEncrypted = ApplySecret(entity.TlsCaCertEncrypted, request.TlsCaCert);
        entity.TlsClientCertEncrypted = ApplySecret(entity.TlsClientCertEncrypted, request.TlsClientCert);
        entity.TlsClientKeyEncrypted = ApplySecret(entity.TlsClientKeyEncrypted, request.TlsClientKey);

        // V2.5 — apply SSH transport fields when the host type is Ssh; clear
        // them otherwise so an old SSH config doesn't shadow a switch back to
        // LocalSocket / TcpTls.
        if (request.HostType == DockerHostType.Ssh)
        {
            entity.SshHost = NormalizeNullableString(request.SshHost);
            entity.SshPort = request.SshPort is > 0 and <= 65535 ? request.SshPort : 22;
            entity.SshUsername = NormalizeNullableString(request.SshUsername);
            entity.SshPrivateKeyEncrypted = ApplySecret(entity.SshPrivateKeyEncrypted, request.SshPrivateKey);
            entity.SshPrivateKeyPassphraseEncrypted = ApplySecret(entity.SshPrivateKeyPassphraseEncrypted, request.SshPrivateKeyPassphrase);
            entity.SshRemoteSocketPath = string.IsNullOrWhiteSpace(request.SshRemoteSocketPath)
                ? DefaultRemoteSocketPath
                : request.SshRemoteSocketPath.Trim();
        }
        else
        {
            entity.SshHost = null;
            entity.SshPort = null;
            entity.SshUsername = null;
            entity.SshPrivateKeyEncrypted = null;
            entity.SshPrivateKeyPassphraseEncrypted = null;
            entity.SshRemoteSocketPath = null;
        }

        entity.UpdatedUtc = DateTime.UtcNow;
    }

    public DockerHostTransport BuildTransport(DockerConnectionPingRequest request, DockerConnectionEntity? existing)
    {
        var caPlain = ResolveSecret(existing?.TlsCaCertEncrypted, request.TlsCaCert);
        var clientCertPlain = ResolveSecret(existing?.TlsClientCertEncrypted, request.TlsClientCert);
        var clientKeyPlain = ResolveSecret(existing?.TlsClientKeyEncrypted, request.TlsClientKey);

        var tls = !string.IsNullOrEmpty(caPlain) || !string.IsNullOrEmpty(clientCertPlain) || !string.IsNullOrEmpty(clientKeyPlain)
            ? new DockerTlsMaterial(caPlain ?? string.Empty, clientCertPlain ?? string.Empty, clientKeyPlain ?? string.Empty)
            : null;

        var ssh = request.HostType == DockerHostType.Ssh
            ? BuildSshFromRequest(request, existing)
            : null;

        return new DockerHostTransport(
            request.HostType,
            request.HostType == DockerHostType.TcpTls ? request.HostUrl : null,
            tls,
            ssh);
    }

    public DockerHostTransport BuildTransport(DockerConnectionEntity entity)
    {
        var tls = BuildTlsFromEncrypted(entity.TlsCaCertEncrypted, entity.TlsClientCertEncrypted, entity.TlsClientKeyEncrypted);
        var ssh = entity.HostType == DockerHostType.Ssh ? BuildSshFromEntity(entity) : null;
        return new DockerHostTransport(entity.HostType, entity.HostUrl, tls, ssh);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private string? ApplySecret(string? existingEncrypted, SecretValueUpsert? upsert)
    {
        if (upsert is null) return existingEncrypted;
        return upsert.Action switch
        {
            SecretValueAction.Keep => existingEncrypted,
            SecretValueAction.Clear => null,
            SecretValueAction.Set => string.IsNullOrEmpty(upsert.Value) ? null : encryption.Encrypt(upsert.Value),
            _ => existingEncrypted,
        };
    }

    private string? ResolveSecret(string? existingEncrypted, SecretValueUpsert? upsert)
    {
        if (upsert is null) return SafeDecrypt(existingEncrypted);
        return upsert.Action switch
        {
            SecretValueAction.Keep => SafeDecrypt(existingEncrypted),
            SecretValueAction.Clear => null,
            SecretValueAction.Set => upsert.Value,
            _ => SafeDecrypt(existingEncrypted),
        };
    }

    private DockerTlsMaterial? BuildTlsFromEncrypted(string? caEnc, string? clientCertEnc, string? clientKeyEnc)
    {
        if (string.IsNullOrEmpty(caEnc) && string.IsNullOrEmpty(clientCertEnc) && string.IsNullOrEmpty(clientKeyEnc))
            return null;
        return new DockerTlsMaterial(
            SafeDecrypt(caEnc) ?? string.Empty,
            SafeDecrypt(clientCertEnc) ?? string.Empty,
            SafeDecrypt(clientKeyEnc) ?? string.Empty);
    }

    private DockerSshCredentials? BuildSshFromEntity(DockerConnectionEntity entity)
    {
        var privateKey = SafeDecrypt(entity.SshPrivateKeyEncrypted);
        if (string.IsNullOrEmpty(entity.SshHost)
            || string.IsNullOrEmpty(entity.SshUsername)
            || string.IsNullOrEmpty(privateKey))
            return null;

        var passphrase = SafeDecrypt(entity.SshPrivateKeyPassphraseEncrypted);
        return new DockerSshCredentials(
            entity.SshHost,
            entity.SshPort ?? 22,
            entity.SshUsername,
            privateKey,
            passphrase,
            string.IsNullOrWhiteSpace(entity.SshRemoteSocketPath) ? DefaultRemoteSocketPath : entity.SshRemoteSocketPath);
    }

    private DockerSshCredentials? BuildSshFromRequest(DockerConnectionPingRequest request, DockerConnectionEntity? existing)
    {
        var host = NormalizeNullableString(request.SshHost) ?? existing?.SshHost;
        var username = NormalizeNullableString(request.SshUsername) ?? existing?.SshUsername;
        var port = request.SshPort is > 0 and <= 65535 ? request.SshPort.Value : existing?.SshPort ?? 22;
        var privateKey = ResolveSecret(existing?.SshPrivateKeyEncrypted, request.SshPrivateKey);
        var passphrase = ResolveSecret(existing?.SshPrivateKeyPassphraseEncrypted, request.SshPrivateKeyPassphrase);
        var remoteSocket = !string.IsNullOrWhiteSpace(request.SshRemoteSocketPath)
            ? request.SshRemoteSocketPath.Trim()
            : (existing?.SshRemoteSocketPath ?? DefaultRemoteSocketPath);

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(privateKey))
            return null;

        return new DockerSshCredentials(host, port, username, privateKey, passphrase, remoteSocket);
    }

    private static string? NormalizeNullableString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private string? SafeDecrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return encryption.Decrypt(encrypted); }
        catch { return null; }
    }

    private static bool HasAnyTls(DockerConnectionEntity entity) =>
        !string.IsNullOrEmpty(entity.TlsCaCertEncrypted)
        || !string.IsNullOrEmpty(entity.TlsClientCertEncrypted)
        || !string.IsNullOrEmpty(entity.TlsClientKeyEncrypted);
}
