using Docker.DotNet;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V2.5 — wraps an <see cref="IDockerClient"/> built against the local end of
/// an SSH tunnel so disposing the client also tears down the tunnel (TCP
/// listener + SSH session). Every operation property delegates to the
/// underlying client; only <see cref="Dispose"/> diverges.
/// </summary>
/// <remarks>
/// Implemented as a hand-written delegating wrapper rather than a runtime
/// proxy so the inner client and tunnel share a single lifetime owner without
/// callers having to learn a new "lease" type — <c>DockerHostClient</c> keeps
/// its existing <c>using (client) { ... }</c> shape.
/// </remarks>
internal sealed class SshTunnelledDockerClient(IDockerClient inner, SshDockerTunnel tunnel) : IDockerClient
{
    public DockerClientConfiguration Configuration => inner.Configuration;
    public TimeSpan DefaultTimeout
    {
        get => inner.DefaultTimeout;
        set => inner.DefaultTimeout = value;
    }
    public IContainerOperations Containers => inner.Containers;
    public IImageOperations Images => inner.Images;
    public ISystemOperations System => inner.System;
    public INetworkOperations Networks => inner.Networks;
    public IVolumeOperations Volumes => inner.Volumes;
    public ISecretsOperations Secrets => inner.Secrets;
    public ISwarmOperations Swarm => inner.Swarm;
    public ITasksOperations Tasks => inner.Tasks;
    public IPluginOperations Plugin => inner.Plugin;
    public IExecOperations Exec => inner.Exec;
    public IConfigOperations Configs => inner.Configs;

    public void Dispose()
    {
        try { inner.Dispose(); } catch { /* idempotent */ }
        try { tunnel.Dispose(); } catch { /* idempotent */ }
    }
}
