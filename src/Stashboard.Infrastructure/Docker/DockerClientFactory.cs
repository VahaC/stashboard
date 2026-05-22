using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docker.DotNet.X509;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Production <see cref="IDockerClientFactory"/>. Local sockets resolve through the
/// platform default URI (npipe on Windows, unix socket on Linux). Remote TCP+TLS
/// goes through a custom <see cref="Credentials"/> that wires the client cert and
/// (optionally) a custom CA root trust without polluting the OS trust store.
/// V2.5 — SSH hosts open a short-lived TCP tunnel (<see cref="SshDockerTunnel"/>)
/// and point Docker.DotNet at <c>tcp://127.0.0.1:{port}</c>; the tunnel is owned
/// by the returned client and torn down when the caller disposes it.
/// </summary>
public sealed class DockerClientFactory(ILogger<DockerClientFactory>? logger = null) : IDockerClientFactory
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public IDockerClient Create(
        DockerHostType hostType,
        string? hostUrl,
        DockerTlsMaterial? tlsMaterial,
        DockerSshCredentials? sshCredentials = null)
    {
        return hostType switch
        {
            DockerHostType.LocalSocket => new DockerClientConfiguration().CreateClient(),
            DockerHostType.TcpTls => BuildRemoteTlsClient(hostUrl, tlsMaterial),
            DockerHostType.Ssh => BuildSshClient(sshCredentials),
            _ => throw new NotSupportedException($"Docker host type {hostType} is not supported."),
        };
    }

    private static IDockerClient BuildRemoteTlsClient(string? hostUrl, DockerTlsMaterial? tlsMaterial)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
            throw new ArgumentException("hostUrl is required for TcpTls Docker hosts.", nameof(hostUrl));
        if (tlsMaterial is null)
            throw new ArgumentException("TLS material is required for TcpTls Docker hosts.", nameof(tlsMaterial));

        var clientCert = LoadClientCertificate(tlsMaterial);
        var credentials = new CertificateCredentials(clientCert);

        if (!string.IsNullOrWhiteSpace(tlsMaterial.CaCert))
        {
            // ManagedHandler (Docker.DotNet's HTTP transport) creates its own SslStream and
            // reads ServerCertificateValidationCallback directly from CertificateCredentials —
            // it never goes through HttpClientHandler.ServerCertificateCustomValidationCallback.
            var caCert = X509Certificate2.CreateFromPem(tlsMaterial.CaCert);
            credentials.ServerCertificateValidationCallback =
                (_, serverCert, _, _) => ValidateAgainstCustomCa(serverCert, caCert);
        }

        return new DockerClientConfiguration(new Uri(hostUrl), credentials).CreateClient();
    }

    private IDockerClient BuildSshClient(DockerSshCredentials? credentials)
    {
        if (credentials is null)
            throw new ArgumentException("SSH credentials are required for Ssh Docker hosts.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.Host))
            throw new ArgumentException("SSH host is required.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.Username))
            throw new ArgumentException("SSH username is required.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.PrivateKeyPem))
            throw new ArgumentException("SSH private key is required.", nameof(credentials));

        var tunnel = SshDockerTunnel.Connect(credentials, _logger);
        try
        {
            var inner = new DockerClientConfiguration(tunnel.LocalUri).CreateClient();
            return new SshTunnelledDockerClient(inner, tunnel);
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    private static X509Certificate2 LoadClientCertificate(DockerTlsMaterial tlsMaterial)
    {
        // X509Certificate2.CreateFromPem returns an "ephemeral" key on Windows that
        // can't be used for client-auth handshakes. Re-importing through PFX bytes
        // fixes that and is harmless on Linux. See dotnet/runtime#23749.
        var pem = X509Certificate2.CreateFromPem(tlsMaterial.ClientCert, tlsMaterial.ClientKey);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }

    private static bool ValidateAgainstCustomCa(X509Certificate? serverCert, X509Certificate2 caCert)
    {
        if (serverCert is not X509Certificate2 cert) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert);
    }
}
