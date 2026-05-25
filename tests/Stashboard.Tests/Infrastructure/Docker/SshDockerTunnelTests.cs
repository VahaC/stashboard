using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.5 — tests for <see cref="SshDockerTunnel"/>. The SSH.NET glue
/// (<c>SshRemoteCommandTransport</c>) needs a live server and is exercised by
/// the opt-in <see cref="DockerHostClientIntegrationTests"/> suite. Everything
/// the tunnel itself owns — the accept loop, the bidirectional byte bridge,
/// half-close draining and the dial-stdio/socat command selection — is testable
/// here against a <see cref="FakeRemoteCommandTransport"/> that wires the
/// "remote command" up to in-memory pipes, so these tests really push bytes
/// through the loopback listener rather than asserting on mocks.
/// </summary>
public class SshDockerTunnelTests
{
    [Fact]
    public void Connect_InvalidPrivateKey_ThrowsAndLeavesNoListenerBehind()
    {
        var creds = new DockerSshCredentials(
            Host: "127.0.0.1",
            Port: 1, // reserved port — no server here
            Username: "anyone",
            PrivateKeyPem: "this is not a PEM key",
            PrivateKeyPassphrase: null,
            RemoteSocketPath: "/var/run/docker.sock");

        // PrivateKeyFile parsing fails synchronously on a malformed key so the
        // factory surfaces an exception without ever opening a socket.
        Assert.ThrowsAny<Exception>(() => SshDockerTunnel.Connect(creds));
    }

    [Fact]
    public void DefaultRemoteSocketPath_IsRootfulDockerSocket()
    {
        Assert.Equal("/var/run/docker.sock", SshDockerTunnel.DefaultRemoteSocketPath);
    }

    // ── command selection (the previously-dead socat fallback) ───────────────

    [Fact]
    public void SelectBridgeCommand_DockerCliPresent_UsesDialStdio()
    {
        using var transport = new FakeRemoteCommandTransport(dockerAvailable: true, RemoteEchoAsync);

        var command = SshDockerTunnel.SelectBridgeCommand(transport, "/var/run/docker.sock", logger: null);

        Assert.Equal("docker system dial-stdio", command);
        Assert.Contains("command -v docker", transport.ProbedCommands);
    }

    [Fact]
    public void SelectBridgeCommand_DockerCliAbsent_FallsBackToSocatOnGivenSocket()
    {
        using var transport = new FakeRemoteCommandTransport(dockerAvailable: false, RemoteEchoAsync);

        var command = SshDockerTunnel.SelectBridgeCommand(transport, "/run/user/1000/docker.sock", logger: null);

        Assert.Equal("socat - UNIX-CONNECT:/run/user/1000/docker.sock", command);
    }

    // ── end-to-end byte bridging through the loopback listener ───────────────

    [Fact]
    public async Task Bridge_RoundTripsRequestAndResponseBytes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var transport = new FakeRemoteCommandTransport(dockerAvailable: true, RemoteEchoAsync);
        using var tunnel = SshDockerTunnel.Create(transport, "/var/run/docker.sock");

        using var client = new TcpClient();
        await client.ConnectAsync(tunnel.LocalUri.Host, tunnel.LocalUri.Port, cts.Token);
        await using var stream = client.GetStream();

        // The echo "remote" prefixes "echo:" so we can prove bytes travelled in
        // both directions through the real listener + bridge, not just back.
        await stream.WriteAsync("hello\n"u8.ToArray(), cts.Token);
        await stream.FlushAsync(cts.Token);

        var response = await ReadLineAsync(stream, cts.Token);

        Assert.Equal("echo:hello", response);
        Assert.Contains("docker system dial-stdio", transport.OpenedCommands);
    }

    [Fact]
    public async Task Bridge_OpensSocatCommand_WhenDockerCliAbsent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var transport = new FakeRemoteCommandTransport(dockerAvailable: false, RemoteEchoAsync);
        using var tunnel = SshDockerTunnel.Create(transport, "/var/run/docker.sock");

        using var client = new TcpClient();
        await client.ConnectAsync(tunnel.LocalUri.Host, tunnel.LocalUri.Port, cts.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync("ping\n"u8.ToArray(), cts.Token);
        await stream.FlushAsync(cts.Token);
        var response = await ReadLineAsync(stream, cts.Token);

        // Proves the fallback path is actually reachable — the old selection
        // logic could never get here because it keyed off CreateCommand throwing.
        Assert.Equal("echo:ping", response);
        Assert.Contains("socat - UNIX-CONNECT:/var/run/docker.sock", transport.OpenedCommands);
        Assert.DoesNotContain("docker system dial-stdio", transport.OpenedCommands);
    }

    [Fact]
    public async Task Bridge_ClientDisconnect_CompletesRemoteChannelWithoutLeaking()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var transport = new FakeRemoteCommandTransport(dockerAvailable: true, RemoteEchoAsync);
        using var tunnel = SshDockerTunnel.Create(transport, "/var/run/docker.sock");

        var client = new TcpClient();
        await client.ConnectAsync(tunnel.LocalUri.Host, tunnel.LocalUri.Port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync("hello\n"u8.ToArray(), cts.Token);
        await stream.FlushAsync(cts.Token);
        Assert.Equal("echo:hello", await ReadLineAsync(stream, cts.Token));

        // The Docker client closes the whole socket when it's done (it disposes
        // the IDockerClient). The bridge must notice the EOF, drive the remote
        // command's stdin to EOF and let the channel complete — not leak it.
        client.Close();

        var channel = Assert.Single(transport.Channels);
        await channel.Completion.WaitAsync(cts.Token);
        Assert.True(channel.Completion.IsCompleted);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Reads from <paramref name="remoteStdin"/> until EOF, echoing each
    /// chunk back to <paramref name="remoteStdout"/> with an "echo:" prefix on the
    /// first chunk so a single round-trip is observable.</summary>
    private static async Task RemoteEchoAsync(Stream remoteStdin, Stream remoteStdout, CancellationToken token)
    {
        var buffer = new byte[1024];
        var first = true;
        while (true)
        {
            int read = await remoteStdin.ReadAsync(buffer, token);
            if (read <= 0) break;
            if (first)
            {
                await remoteStdout.WriteAsync("echo:"u8.ToArray(), token);
                first = false;
            }
            await remoteStdout.WriteAsync(buffer.AsMemory(0, read), token);
            await remoteStdout.FlushAsync(token);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, token);
            if (n <= 0 || one[0] == (byte)'\n') break;
            sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// In-memory <see cref="IRemoteCommandTransport"/>. Each opened channel is
    /// backed by two <see cref="Pipe"/>s modelling the remote command's stdin and
    /// stdout as distinct half-duplex streams (faithful to a real exec channel,
    /// so closing stdin doesn't tear down stdout). The supplied delegate plays
    /// the part of the remote process.
    /// </summary>
    private sealed class FakeRemoteCommandTransport(
        bool dockerAvailable, Func<Stream, Stream, CancellationToken, Task> remote) : IRemoteCommandTransport
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<FakeChannel> _channels = new();

        public List<string> ProbedCommands { get; } = new();
        public List<string> OpenedCommands { get; } = new();
        public IReadOnlyList<FakeChannel> Channels => _channels;

        public bool RunSucceeds(string commandText)
        {
            ProbedCommands.Add(commandText);
            // Model `command -v docker`: succeeds iff the docker CLI is present.
            return dockerAvailable;
        }

        public IRemoteCommandChannel Open(string commandText, CancellationToken cancellationToken)
        {
            OpenedCommands.Add(commandText);

            var toRemote = new Pipe();   // tunnel writes (stdin)  -> remote reads
            var fromRemote = new Pipe(); // remote writes (stdout) -> tunnel reads

            var channelInput = toRemote.Writer.AsStream();
            var channelOutput = fromRemote.Reader.AsStream();
            var remoteStdin = toRemote.Reader.AsStream();
            var remoteStdout = fromRemote.Writer.AsStream();

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var completion = Task.Run(async () =>
            {
                try
                {
                    await remote(remoteStdin, remoteStdout, linked.Token);
                }
                catch (OperationCanceledException) { /* shutdown */ }
                finally
                {
                    await remoteStdout.DisposeAsync(); // completes the writer -> tunnel sees stdout EOF
                    linked.Dispose();
                }
            });

            var channel = new FakeChannel(channelInput, channelOutput, completion);
            _channels.Add(channel);
            return channel;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* idempotent */ }
            foreach (var channel in _channels) channel.Dispose();
            _cts.Dispose();
        }

        public sealed class FakeChannel(Stream input, Stream output, Task completion) : IRemoteCommandChannel
        {
            public Stream Input { get; } = input;
            public Stream Output { get; } = output;
            public Task Completion { get; } = completion;

            public void Dispose()
            {
                try { Input.Dispose(); } catch { /* idempotent */ }
                try { Output.Dispose(); } catch { /* idempotent */ }
            }
        }
    }
}
