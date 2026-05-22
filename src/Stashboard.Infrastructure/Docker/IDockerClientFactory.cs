using Docker.DotNet;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Builds an <see cref="IDockerClient"/> against a local socket, a remote
/// TCP+TLS daemon, or a remote daemon reachable over an SSH tunnel. Extracted
/// so <see cref="DockerHostClient"/> can be tested against a mock daemon
/// without going through the real connection plumbing.
/// </summary>
/// <remarks>
/// V2.5 — SSH credentials are surfaced as an optional parameter rather than a
/// new <c>Create</c> overload so all existing call sites continue to compile
/// without ceremony. The SSH-tunnelled client owns the underlying tunnel; the
/// caller only needs to <c>Dispose</c> the returned <see cref="IDockerClient"/>
/// to tear everything down (TCP listener + SSH session).
/// </remarks>
public interface IDockerClientFactory
{
    IDockerClient Create(
        DockerHostType hostType,
        string? hostUrl,
        DockerTlsMaterial? tlsMaterial,
        DockerSshCredentials? sshCredentials = null);
}
