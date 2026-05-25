using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services.HostShell;

/// <summary>
/// V5.3 — tests for the <see cref="HostShellSession"/> byte pump. The SSH PTY
/// and the WebSocket are both faked with in-memory duplex pipes so these tests
/// push real bytes through the pump and assert on the round-trip, byte counts,
/// resize dispatch and the various end reasons — the same fake-transport
/// approach that keeps <c>SshDockerTunnel</c> testable.
/// </summary>
public class HostShellSessionTests
{
    private static readonly TimeSpan NoIdleTimeout = Timeout.InfiniteTimeSpan;

    [Fact]
    public async Task RoundTrips_ClientInputToHost_AndHostOutputToClient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = new FakeHostShellChannel();
        var client = new FakeClientTransport();

        var run = HostShellSession.RunAsync(host, client, NoIdleTimeout, logger: null, cts.Token);

        // Browser → host.
        client.Enqueue(new ShellClientMessage.Input(Encoding.UTF8.GetBytes("ls\n")));
        var received = await host.ReadHostInputAsync(3, cts.Token);
        Assert.Equal("ls\n", Encoding.UTF8.GetString(received));

        // Host → browser.
        await host.WriteHostOutputAsync(Encoding.UTF8.GetBytes("total 0\n"), cts.Token);
        var echoed = await client.WaitForBytesAsync(8, cts.Token);
        Assert.Equal("total 0\n", Encoding.UTF8.GetString(echoed));

        client.Enqueue(new ShellClientMessage.Closed());
        var result = await run;

        Assert.Equal(HostShellSessionEndReason.ClosedByClient, result.EndReason);
        Assert.Equal(3, result.BytesFromClient);
        Assert.Equal(8, result.BytesToClient);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RemoteShellExit_EndsWithRemoteClosed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = new FakeHostShellChannel();
        var client = new FakeClientTransport();

        var run = HostShellSession.RunAsync(host, client, NoIdleTimeout, logger: null, cts.Token);

        host.CompleteRemote(); // the PTY closed (user typed `exit`)

        var result = await run;
        Assert.Equal(HostShellSessionEndReason.RemoteClosed, result.EndReason);
    }

    [Fact]
    public async Task NoActivity_FiresIdleTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = new FakeHostShellChannel();
        var client = new FakeClientTransport();

        var result = await HostShellSession.RunAsync(
            host, client, idleTimeout: TimeSpan.FromMilliseconds(150), logger: null, cts.Token);

        Assert.Equal(HostShellSessionEndReason.IdleTimeout, result.EndReason);
        // The pump reports the reason; closing the socket is the controller's job.
    }

    [Fact]
    public async Task Resize_IsDispatchedToTheChannel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = new FakeHostShellChannel();
        var client = new FakeClientTransport();

        var run = HostShellSession.RunAsync(host, client, NoIdleTimeout, logger: null, cts.Token);

        client.Enqueue(new ShellClientMessage.Resize(120, 40));
        await host.WaitForResizeAsync(cts.Token);

        client.Enqueue(new ShellClientMessage.Closed());
        await run;

        Assert.Contains((120u, 40u), host.Resizes);
    }

    [Fact]
    public async Task ExternalCancellation_EndsAsServerClose()
    {
        using var cts = new CancellationTokenSource();
        using var host = new FakeHostShellChannel();
        var client = new FakeClientTransport();

        var run = HostShellSession.RunAsync(host, client, NoIdleTimeout, logger: null, cts.Token);
        cts.Cancel(); // app shutting down

        var result = await run;
        Assert.Equal(HostShellSessionEndReason.ClosedByServer, result.EndReason);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="IHostShellChannel"/>. Two pipes model the PTY's two
    /// directions: the session reads host output and writes browser input through
    /// a single duplex <see cref="Stream"/>; the test drives the other ends.
    /// </summary>
    private sealed class FakeHostShellChannel : IHostShellChannel
    {
        private readonly Pipe _hostToBrowser = new(); // test writes  -> session reads
        private readonly Pipe _browserToHost = new(); // session writes -> test reads
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<bool> _resizeSignals = Channel.CreateUnbounded<bool>();

        public Stream Stream { get; }
        public Task Completion => _completion.Task;
        public List<(uint Columns, uint Rows)> Resizes { get; } = new();

        public FakeHostShellChannel()
        {
            Stream = new DuplexStream(
                readSide: _hostToBrowser.Reader.AsStream(),
                writeSide: _browserToHost.Writer.AsStream());
        }

        public bool TryResize(uint columns, uint rows)
        {
            Resizes.Add((columns, rows));
            _resizeSignals.Writer.TryWrite(true);
            return false; // mirrors SSH.NET 2024.2.0 (no live window-change)
        }

        public async Task<byte[]> ReadHostInputAsync(int count, CancellationToken ct)
        {
            var reader = _browserToHost.Reader.AsStream();
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(read), ct);
                if (n <= 0) break;
                read += n;
            }
            return buffer[..read];
        }

        public async Task WriteHostOutputAsync(byte[] data, CancellationToken ct)
        {
            var writer = _hostToBrowser.Writer.AsStream();
            await writer.WriteAsync(data, ct);
            await writer.FlushAsync(ct);
        }

        public async Task WaitForResizeAsync(CancellationToken ct) => await _resizeSignals.Reader.ReadAsync(ct);

        public void CompleteRemote()
        {
            _hostToBrowser.Writer.Complete();
            _completion.TrySetResult();
        }

        public void Dispose() => _completion.TrySetResult();
    }

    private sealed class FakeClientTransport : IShellClientTransport
    {
        private readonly Channel<ShellClientMessage> _inbound = Channel.CreateUnbounded<ShellClientMessage>();
        private readonly List<byte> _sent = new();
        private readonly object _gate = new();

        public string? CloseReason { get; private set; }

        public void Enqueue(ShellClientMessage message) => _inbound.Writer.TryWrite(message);

        public async ValueTask<ShellClientMessage> ReceiveAsync(CancellationToken cancellationToken)
            => await _inbound.Reader.ReadAsync(cancellationToken);

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            lock (_gate) _sent.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            CloseReason = reason;
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> WaitForBytesAsync(int count, CancellationToken ct)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_sent.Count >= count) return _sent.GetRange(0, count).ToArray();
                }
                await Task.Delay(10, ct);
            }
        }
    }

    /// <summary>A read side + a write side stitched into one duplex stream.</summary>
    private sealed class DuplexStream(Stream readSide, Stream writeSide) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => readSide.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => readSide.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => writeSide.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => writeSide.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => writeSide.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writeSide.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
