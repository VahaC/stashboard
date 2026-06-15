using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Mapping;

/// <summary>
/// Unit tests for <see cref="DockerConnectionMapper"/>. Focuses on the V2.5
/// SSH paths (entity persistence, tri-state secret handling, transport
/// projection) since the V1/V2.4 TLS paths were exercised exclusively via
/// the controller integration tests. Encryption is mocked so the encrypted
/// payloads remain predictable.
/// </summary>
public class DockerConnectionMapperTests
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly DockerConnectionMapper _mapper;

    public DockerConnectionMapperTests()
    {
        _encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        _mapper = new DockerConnectionMapper(_encryption.Object);
    }

    // ── ToResponse — presence flags ──────────────────────────────────────────

    [Fact]
    public void ToResponse_SshConnection_SurfacesPlainFieldsAndPresenceFlags()
    {
        var entity = SshEntity(withPassphrase: true);

        var response = _mapper.ToResponse(entity, usageCount: 3);

        Assert.Equal(DockerHostType.Ssh, response.HostType);
        Assert.Equal("vps.example.com", response.SshHost);
        Assert.Equal(2200, response.SshPort);
        Assert.Equal("docker", response.SshUsername);
        Assert.True(response.HasSshPrivateKey);
        Assert.True(response.HasSshPrivateKeyPassphrase);
        Assert.Equal("/var/run/docker.sock", response.SshRemoteSocketPath);
        Assert.Equal(3, response.UsageCount);
    }

    [Fact]
    public void ToResponse_SshConnectionWithoutPassphrase_PassphraseFlagFalse()
    {
        var entity = SshEntity(withPassphrase: false);

        var response = _mapper.ToResponse(entity, usageCount: 0);

        Assert.True(response.HasSshPrivateKey);
        Assert.False(response.HasSshPrivateKeyPassphrase);
    }

    [Fact]
    public void ToResponse_NeverIncludesDecryptedSshSecrets()
    {
        var entity = SshEntity(withPassphrase: true);

        var serialized = System.Text.Json.JsonSerializer.Serialize(_mapper.ToResponse(entity, 0));

        // PEM content + passphrase must never appear in the response payload.
        Assert.DoesNotContain("PRIVATE KEY", serialized);
        Assert.DoesNotContain("hunter2", serialized);
    }

    // ── ApplyUpsert — host type switching ────────────────────────────────────

    [Fact]
    public void ApplyUpsert_SwitchToSsh_PersistsSshFieldsAndDefaultsPortAndSocketPath()
    {
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = SshUpsert(host: "vps", port: null, username: "docker",
            socketPath: null, keyAction: SecretValueAction.Set, keyValue: "PEM");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(DockerHostType.Ssh, entity.HostType);
        Assert.Equal("vps", entity.SshHost);
        Assert.Equal(22, entity.SshPort);
        Assert.Equal("docker", entity.SshUsername);
        Assert.Equal("enc:PEM", entity.SshPrivateKeyEncrypted);
        Assert.Equal("/var/run/docker.sock", entity.SshRemoteSocketPath);
    }

    [Fact]
    public void ApplyUpsert_SwitchAwayFromSsh_ClearsAllSshColumns()
    {
        var entity = SshEntity(withPassphrase: true);
        var request = new DockerConnectionUpsertRequest(
            Name: "renamed",
            HostType: DockerHostType.LocalSocket,
            HostUrl: null,
            TlsCaCert: null,
            TlsClientCert: null,
            TlsClientKey: null);

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.SshHost);
        Assert.Null(entity.SshPort);
        Assert.Null(entity.SshUsername);
        Assert.Null(entity.SshPrivateKeyEncrypted);
        Assert.Null(entity.SshPrivateKeyPassphraseEncrypted);
        Assert.Null(entity.SshRemoteSocketPath);
    }

    [Fact]
    public void ApplyUpsert_SshKeepAction_PreservesExistingEncryptedKey()
    {
        var entity = SshEntity(withPassphrase: true);
        var originalKey = entity.SshPrivateKeyEncrypted;
        var originalPassphrase = entity.SshPrivateKeyPassphraseEncrypted;

        var request = SshUpsert(host: "vps.example.com", port: 2200, username: "docker",
            socketPath: "/var/run/docker.sock",
            keyAction: SecretValueAction.Keep, keyValue: null,
            passphraseAction: SecretValueAction.Keep, passphraseValue: null);

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(originalKey, entity.SshPrivateKeyEncrypted);
        Assert.Equal(originalPassphrase, entity.SshPrivateKeyPassphraseEncrypted);
    }

    [Fact]
    public void ApplyUpsert_SshClearAction_DropsTheEncryptedKey()
    {
        var entity = SshEntity(withPassphrase: true);

        var request = SshUpsert(host: "vps.example.com", port: 2200, username: "docker",
            socketPath: "/var/run/docker.sock",
            keyAction: SecretValueAction.Clear, keyValue: null,
            passphraseAction: SecretValueAction.Clear, passphraseValue: null);

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.SshPrivateKeyEncrypted);
        Assert.Null(entity.SshPrivateKeyPassphraseEncrypted);
    }

    [Fact]
    public void ApplyUpsert_SshPortOutOfRange_FallsBackToDefault22()
    {
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = SshUpsert(host: "vps", port: 100_000, username: "docker",
            socketPath: null, keyAction: SecretValueAction.Set, keyValue: "PEM");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(22, entity.SshPort);
    }

    // ── BuildTransport ──────────────────────────────────────────────────────

    [Fact]
    public void BuildTransport_FromSshEntity_PopulatesSshCredentialsAndOmitsTls()
    {
        var entity = SshEntity(withPassphrase: true);

        var transport = _mapper.BuildTransport(entity);

        Assert.Equal(DockerHostType.Ssh, transport.HostType);
        Assert.Null(transport.Tls);
        Assert.NotNull(transport.Ssh);
        Assert.Equal("vps.example.com", transport.Ssh!.Host);
        Assert.Equal(2200, transport.Ssh.Port);
        Assert.Equal("docker", transport.Ssh.Username);
        Assert.Contains("PRIVATE KEY", transport.Ssh.PrivateKeyPem);
        Assert.Equal("hunter2", transport.Ssh.PrivateKeyPassphrase);
        Assert.Equal("/var/run/docker.sock", transport.Ssh.RemoteSocketPath);
    }

    [Fact]
    public void BuildTransport_FromSshPingRequest_ResolvesKeepSecretsFromEntity()
    {
        var existing = SshEntity(withPassphrase: false);
        var request = new DockerConnectionPingRequest(
            HostType: DockerHostType.Ssh,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            SshHost: "vps.example.com",
            SshPort: 2200,
            SshUsername: "docker",
            SshPrivateKey: new SecretValueUpsert(SecretValueAction.Keep, null),
            SshPrivateKeyPassphrase: null,
            SshRemoteSocketPath: "/var/run/docker.sock");

        var transport = _mapper.BuildTransport(request, existing);

        Assert.NotNull(transport.Ssh);
        Assert.Contains("PRIVATE KEY", transport.Ssh!.PrivateKeyPem);
    }

    [Fact]
    public void BuildTransport_FromSshPingRequest_SetSecretReplacesPreviousValue()
    {
        var existing = SshEntity(withPassphrase: false);
        var request = new DockerConnectionPingRequest(
            HostType: DockerHostType.Ssh,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            SshHost: "vps.example.com",
            SshPort: 22,
            SshUsername: "docker",
            SshPrivateKey: new SecretValueUpsert(SecretValueAction.Set, "FRESH-KEY"),
            SshPrivateKeyPassphrase: null,
            SshRemoteSocketPath: null);

        var transport = _mapper.BuildTransport(request, existing);

        Assert.NotNull(transport.Ssh);
        Assert.Equal("FRESH-KEY", transport.Ssh!.PrivateKeyPem);
    }

    [Fact]
    public void BuildTransport_SshEntityMissingRequiredField_ReturnsNullSshAndPreservesHostType()
    {
        var entity = SshEntity(withPassphrase: false);
        entity.SshUsername = null; // missing required field

        var transport = _mapper.BuildTransport(entity);

        Assert.Equal(DockerHostType.Ssh, transport.HostType);
        Assert.Null(transport.Ssh);
    }

    [Fact]
    public void BuildTransport_NonSshEntity_OmitsSshFields()
    {
        var entity = new DockerConnectionEntity
        {
            Name = "local",
            HostType = DockerHostType.LocalSocket,
        };

        var transport = _mapper.BuildTransport(entity);

        Assert.Null(transport.Ssh);
        Assert.Null(transport.Tls);
        Assert.Null(transport.HostUrl);
    }

    // ── V7.1 — Compose path mapping ──────────────────────────────────────────

    [Fact]
    public void ApplyUpsert_LocalSocketWithComposePathMapping_PersistsTrimmedPrefixes()
    {
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = new DockerConnectionUpsertRequest(
            Name: "home",
            HostType: DockerHostType.LocalSocket,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            ComposePathHostPrefix: "  /opt/stacks  ",
            ComposePathContainerPrefix: "  /compose  ");

        _mapper.ApplyUpsert(entity, request);

        Assert.Equal(DockerHostType.LocalSocket, entity.HostType);
        Assert.Equal("/opt/stacks", entity.ComposePathHostPrefix);
        Assert.Equal("/compose", entity.ComposePathContainerPrefix);
    }

    [Fact]
    public void ApplyUpsert_SwitchToRemoteHost_ClearsComposePathMapping()
    {
        // The mapping translates host paths into the Stashboard container's
        // bind-mount paths, so it's meaningless on a remote transport (SSH
        // uses the label path directly; TcpTls has no file access) and must
        // not shadow the switch.
        var entity = new DockerConnectionEntity
        {
            Name = "x",
            HostType = DockerHostType.LocalSocket,
            ComposePathHostPrefix = "/opt/stacks",
            ComposePathContainerPrefix = "/compose",
        };
        var request = new DockerConnectionUpsertRequest(
            Name: "home",
            HostType: DockerHostType.TcpTls,
            HostUrl: "tcp://h:2376",
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            ComposePathHostPrefix: "/should-be-ignored",
            ComposePathContainerPrefix: "/also-ignored");

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.ComposePathHostPrefix);
        Assert.Null(entity.ComposePathContainerPrefix);
    }

    [Fact]
    public void ApplyUpsert_SshHost_ClearsComposePathMapping()
    {
        // V7.1 — SSH connections read the working_dir label path on the host
        // directly; no mapping is stored for them.
        var entity = new DockerConnectionEntity
        {
            Name = "x",
            HostType = DockerHostType.LocalSocket,
            ComposePathHostPrefix = "/opt/stacks",
            ComposePathContainerPrefix = "/compose",
        };
        var request = new DockerConnectionUpsertRequest(
            Name: "vps-prod",
            HostType: DockerHostType.Ssh,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            SshHost: "vps", SshPort: 22, SshUsername: "docker",
            SshPrivateKey: new SecretValueUpsert(SecretValueAction.Set, "PEM"),
            ComposePathHostPrefix: "/should-be-ignored",
            ComposePathContainerPrefix: "/also-ignored");

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.ComposePathHostPrefix);
        Assert.Null(entity.ComposePathContainerPrefix);
    }

    [Fact]
    public void ApplyUpsert_BlankComposePathPrefixes_NormalizeToNull()
    {
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = new DockerConnectionUpsertRequest(
            Name: "home",
            HostType: DockerHostType.LocalSocket,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            ComposePathHostPrefix: "   ",
            ComposePathContainerPrefix: "   ");

        _mapper.ApplyUpsert(entity, request);

        Assert.Null(entity.ComposePathHostPrefix);
        Assert.Null(entity.ComposePathContainerPrefix);
    }

    // ── V5.3 — host terminal opt-in ──────────────────────────────────────────

    [Fact]
    public void ApplyUpsert_SshWithAllowHostShell_PersistsTheFlag()
    {
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = SshUpsert(host: "vps", port: 22, username: "docker",
            socketPath: null, keyAction: SecretValueAction.Set, keyValue: "PEM",
            allowHostShell: true);

        _mapper.ApplyUpsert(entity, request);

        Assert.True(entity.AllowHostShell);
    }

    [Fact]
    public void ApplyUpsert_SwitchAwayFromSsh_ClearsAllowHostShell()
    {
        // The host terminal is SSH-only — a stale opt-in must not survive a
        // switch back to a local socket / TCP host.
        var entity = SshEntity(withPassphrase: false);
        entity.AllowHostShell = true;
        var request = new DockerConnectionUpsertRequest(
            Name: "renamed",
            HostType: DockerHostType.LocalSocket,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null);

        _mapper.ApplyUpsert(entity, request);

        Assert.False(entity.AllowHostShell);
    }

    [Fact]
    public void ToResponse_SurfacesAllowHostShell()
    {
        var entity = SshEntity(withPassphrase: false);
        entity.AllowHostShell = true;

        var response = _mapper.ToResponse(entity, usageCount: 0);

        Assert.True(response.AllowHostShell);
    }

    // ── V5.7 — container-exec opt-in ─────────────────────────────────────────

    [Theory]
    [InlineData(DockerHostType.LocalSocket)]
    [InlineData(DockerHostType.Ssh)]
    public void ApplyUpsert_AllowExec_PersistsForEveryHostType(DockerHostType hostType)
    {
        // Unlike the SSH-only host terminal, container exec is available for any
        // host type, so the opt-in must persist regardless of transport.
        var entity = new DockerConnectionEntity { Name = "x" };
        var request = hostType == DockerHostType.Ssh
            ? SshUpsert(host: "vps", port: 22, username: "docker", socketPath: null,
                keyAction: SecretValueAction.Set, keyValue: "PEM", allowExec: true)
            : new DockerConnectionUpsertRequest(
                Name: "local",
                HostType: DockerHostType.LocalSocket,
                HostUrl: null,
                TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
                AllowExec: true);

        _mapper.ApplyUpsert(entity, request);

        Assert.True(entity.AllowExec);
    }

    [Fact]
    public void ApplyUpsert_AllowExecOff_PersistsFalse()
    {
        var entity = new DockerConnectionEntity { Name = "x", AllowExec = true };
        var request = new DockerConnectionUpsertRequest(
            Name: "x",
            HostType: DockerHostType.LocalSocket,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            AllowExec: false);

        _mapper.ApplyUpsert(entity, request);

        Assert.False(entity.AllowExec);
    }

    [Fact]
    public void ToResponse_SurfacesAllowExec()
    {
        var entity = SshEntity(withPassphrase: false);
        entity.AllowExec = true;

        var response = _mapper.ToResponse(entity, usageCount: 0);

        Assert.True(response.AllowExec);
    }

    [Fact]
    public void ToResponse_SurfacesComposePathMapping()
    {
        var entity = new DockerConnectionEntity
        {
            Name = "home",
            HostType = DockerHostType.LocalSocket,
            ComposePathHostPrefix = "/opt/stacks",
            ComposePathContainerPrefix = "/compose",
        };

        var response = _mapper.ToResponse(entity, usageCount: 0);

        Assert.Equal("/opt/stacks", response.ComposePathHostPrefix);
        Assert.Equal("/compose", response.ComposePathContainerPrefix);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private DockerConnectionEntity SshEntity(bool withPassphrase) => new()
    {
        Id = Guid.NewGuid(),
        Name = "vps-prod",
        HostType = DockerHostType.Ssh,
        SshHost = "vps.example.com",
        SshPort = 2200,
        SshUsername = "docker",
        SshPrivateKeyEncrypted = "enc:-----BEGIN OPENSSH PRIVATE KEY-----\nFAKE\n-----END OPENSSH PRIVATE KEY-----",
        SshPrivateKeyPassphraseEncrypted = withPassphrase ? "enc:hunter2" : null,
        SshRemoteSocketPath = "/var/run/docker.sock",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static DockerConnectionUpsertRequest SshUpsert(
        string host, int? port, string username, string? socketPath,
        SecretValueAction keyAction, string? keyValue,
        SecretValueAction passphraseAction = SecretValueAction.Keep, string? passphraseValue = null,
        bool allowHostShell = false, bool allowExec = false) =>
        new(
            Name: "vps-prod",
            HostType: DockerHostType.Ssh,
            HostUrl: null,
            TlsCaCert: null, TlsClientCert: null, TlsClientKey: null,
            SshHost: host,
            SshPort: port,
            SshUsername: username,
            SshPrivateKey: new SecretValueUpsert(keyAction, keyValue),
            SshPrivateKeyPassphrase: new SecretValueUpsert(passphraseAction, passphraseValue),
            SshRemoteSocketPath: socketPath,
            AllowHostShell: allowHostShell,
            AllowExec: allowExec);
}
