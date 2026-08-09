using System.Text;
using System.Threading.Channels;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services.ProxmoxConsole;

/// <summary>
/// V8.6 — tests for the <see cref="ProxmoxVncSession"/> byte pump. Both ends of the
/// relay (the browser socket and the Proxmox vncwebsocket) are faked with in-memory
/// channels so these tests push real RFB-like bytes through the pump and assert the
/// stream is forwarded <strong>verbatim</strong> in both directions, the byte counts
/// are right, and the various end reasons are reported. Mirrors the V6.6
/// <c>HostShellSessionTests</c>; the faked Proxmox <c>vncwebsocket</c> is exactly the
/// "faked Proxmox vncwebsocket" the phase's test plan calls for.
/// </summary>
public class ProxmoxVncSessionTests
{
    private static readonly TimeSpan NoIdleTimeout = Timeout.InfiniteTimeSpan;

    [Fact]
    public async Task ForwardsRfbBytesVerbatim_BothDirections()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new FakeVncChannel();
        var host = new FakeVncChannel();

        var run = ProxmoxVncSession.RunAsync(client, host, NoIdleTimeout, logger: null, cts.Token);

        // Browser → host (e.g. an RFB client→server message: pointer / key event).
        var clientBytes = new byte[] { 0x03, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        client.EnqueueIncoming(clientBytes);
        var atHost = await host.WaitForWrittenAsync(clientBytes.Length, cts.Token);
        Assert.Equal(clientBytes, atHost);

        // Host → browser (e.g. an RFB FramebufferUpdate). Includes a 0x00 byte to
        // prove there's no text/UTF-8 transform anywhere on the path.
        var hostBytes = Encoding.ASCII.GetBytes("RFB 003.008\n").Concat(new byte[] { 0x00, 0xFF }).ToArray();
        host.EnqueueIncoming(hostBytes);
        var atClient = await client.WaitForWrittenAsync(hostBytes.Length, cts.Token);
        Assert.Equal(hostBytes, atClient);

        client.Close();
        var result = await run;

        Assert.Equal(HostShellSessionEndReason.ClosedByClient, result.EndReason);
        Assert.Equal(clientBytes.Length, result.BytesFromClient);
        Assert.Equal(hostBytes.Length, result.BytesToClient);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProxmoxSideClosing_EndsWithRemoteClosed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new FakeVncChannel();
        var host = new FakeVncChannel();

        var run = ProxmoxVncSession.RunAsync(client, host, NoIdleTimeout, logger: null, cts.Token);

        host.Close(); // Proxmox closed the vncwebsocket (VM powered off / stopped)

        var result = await run;
        Assert.Equal(HostShellSessionEndReason.RemoteClosed, result.EndReason);
    }

    [Fact]
    public async Task NoActivity_FiresIdleTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new FakeVncChannel();
        var host = new FakeVncChannel();

        var result = await ProxmoxVncSession.RunAsync(
            client, host, idleTimeout: TimeSpan.FromMilliseconds(150), logger: null, cts.Token);

        Assert.Equal(HostShellSessionEndReason.IdleTimeout, result.EndReason);
    }

    [Fact]
    public async Task ExternalCancellation_EndsAsServerClose()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeVncChannel();
        var host = new FakeVncChannel();

        var run = ProxmoxVncSession.RunAsync(client, host, NoIdleTimeout, logger: null, cts.Token);
        cts.Cancel(); // app shutting down

        var result = await run;
        Assert.Equal(HostShellSessionEndReason.ClosedByServer, result.EndReason);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────

    /// <summary>In-memory <see cref="IProxmoxVncChannel"/>: a queue of inbound
    /// frames the pump reads (a <c>null</c> sentinel = close), and a buffer of what
    /// the pump wrote, which the test inspects.</summary>
    private sealed class FakeVncChannel : IProxmoxVncChannel
    {
        private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
        private readonly List<byte> _written = new();
        private readonly object _gate = new();

        public void EnqueueIncoming(byte[] data) => _incoming.Writer.TryWrite(data);
        public void Close() => _incoming.Writer.TryWrite(null);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame is null) return 0; // peer closed
            frame.AsMemory().CopyTo(buffer);
            return frame.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            lock (_gate) _written.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<byte[]> WaitForWrittenAsync(int count, CancellationToken ct)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_written.Count >= count) return _written.GetRange(0, count).ToArray();
                }
                await Task.Delay(10, ct);
            }
        }
    }
}


