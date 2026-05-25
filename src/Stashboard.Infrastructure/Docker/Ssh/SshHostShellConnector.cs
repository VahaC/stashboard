using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V5.3 — production <see cref="IHostShellConnector"/>. Opens a single SSH
/// connection to the host and starts an interactive <c>xterm</c> PTY via
/// SSH.NET's <see cref="SshClient.CreateShellStream(string, uint, uint, uint, uint, int)"/>.
/// Reuses <see cref="SshPrivateKeyLoader"/> for key parsing — the same path the
/// V2.5 Docker tunnel uses.
/// </summary>
/// <remarks>
/// This is the host-terminal analogue of <c>SshRemoteCommandTransport</c>: the
/// SSH.NET glue lives here (needs a live server, exercised by the opt-in
/// integration suite) while the WebSocket ↔ byte pump is orchestrated above the
/// <see cref="IHostShellChannel"/> seam so it stays unit-testable.
///
/// I/O note: <see cref="ShellStream"/> does <em>not</em> behave well when driven
/// through the generic <see cref="Stream"/> async wrappers
/// (<c>ReadAsync</c>/<c>WriteAsync</c>) — output can fail to surface and input
/// can fail to flush. The idiomatic, reliable pattern is used instead: read
/// output via the <see cref="ShellStream.DataReceived"/> event, and write input
/// with the synchronous <see cref="Stream.Write(byte[], int, int)"/> +
/// <see cref="Stream.Flush"/>. The duplex <see cref="Stream"/> the bridge sees
/// is a thin adapter over those.
///
/// Live resize: SSH.NET 2024.2.0 does not expose a <c>window-change</c> request
/// on <see cref="ShellStream"/>, so <c>TryResize</c> is a no-op returning
/// <c>false</c>. The PTY is created at the size the browser reports on connect,
/// which is what matters in practice — only live auto-resize is unavailable.
/// </remarks>
public sealed class SshHostShellConnector(ILogger<SshHostShellConnector>? logger = null) : IHostShellConnector
{
    /// <summary>PTY buffer for the ShellStream's internal ring buffer.</summary>
    private const int ShellBufferSize = 8 * 1024;

    public IHostShellChannel Connect(
        DockerSshCredentials credentials, HostShellWindow window, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var keyFile = SshPrivateKeyLoader.Load(credentials);
        var ssh = new SshClient(credentials.Host, credentials.Port, credentials.Username, keyFile)
        {
            // Unlike the short-lived Docker tunnel, an interactive shell can sit
            // idle for minutes between keystrokes — keep the session warm so the
            // server doesn't drop it before the app's own idle timeout fires.
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };

        try
        {
            ssh.Connect();

            // xterm always reports real dimensions, but guard against a 0 that
            // would make the remote shell render into a degenerate window.
            var columns = window.Columns == 0 ? 80u : window.Columns;
            var rows = window.Rows == 0 ? 24u : window.Rows;

            var shell = ssh.CreateShellStream(
                terminalName: "xterm-256color",
                columns: columns,
                rows: rows,
                width: 0,
                height: 0,
                bufferSize: ShellBufferSize);

            return new SshHostShellChannel(ssh, shell, logger);
        }
        catch
        {
            ssh.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Binds a <see cref="ShellStream"/> to the owning <see cref="SshClient"/> and
    /// exposes it as a duplex <see cref="Stream"/> — reads drain the PTY output
    /// (fed by the <see cref="ShellStream.DataReceived"/> event into a channel),
    /// writes forward keystrokes synchronously. <see cref="Completion"/> fires when
    /// the remote shell closes.
    /// </summary>
    private sealed class SshHostShellChannel : IHostShellChannel
    {
        private readonly SshClient _ssh;
        private readonly ShellStream _shell;
        private readonly ILogger? _logger;
        private readonly Channel<byte[]> _output =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ShellDuplexStream _stream;
        private int _disposed;

        public Stream Stream => _stream;
        public Task Completion => _completion.Task;

        public SshHostShellChannel(SshClient ssh, ShellStream shell, ILogger? logger)
        {
            _ssh = ssh;
            _shell = shell;
            _logger = logger;
            _stream = new ShellDuplexStream(shell, _output.Reader);
            // Subscribe before anyone reads so the first prompt isn't missed.
            _shell.DataReceived += OnDataReceived;
            _shell.Closed += OnShellClosed;
        }

        private void OnDataReceived(object? sender, ShellDataEventArgs e)
        {
            var data = e.Data;
            if (data is { Length: > 0 })
                // Clone — the event buffer may be pooled/reused by SSH.NET.
                _output.Writer.TryWrite((byte[])data.Clone());
        }

        private void OnShellClosed(object? sender, EventArgs e)
        {
            _output.Writer.TryComplete();
            _completion.TrySetResult();
        }

        public bool TryResize(uint columns, uint rows) => false;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _completion.TrySetResult();
            _output.Writer.TryComplete();
            try { _shell.DataReceived -= OnDataReceived; } catch { /* idempotent */ }
            try { _shell.Closed -= OnShellClosed; } catch { /* idempotent */ }
            try { _shell.Dispose(); } catch (Exception ex) { _logger?.LogDebug(ex, "Disposing host shell stream failed."); }
            try { _ssh.Dispose(); } catch (Exception ex) { _logger?.LogDebug(ex, "Disposing host shell SSH client failed."); }
        }
    }

    /// <summary>
    /// Duplex adapter over a <see cref="ShellStream"/>: reads come from the
    /// PTY-output channel (fed by <c>DataReceived</c>), writes go straight to the
    /// shell via the synchronous, reliable <c>Write</c> + <c>Flush</c> path.
    /// </summary>
    private sealed class ShellDuplexStream(ShellStream shell, ChannelReader<byte[]> output) : Stream
    {
        private ReadOnlyMemory<byte> _leftover = ReadOnlyMemory<byte>.Empty;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_leftover.IsEmpty)
            {
                try
                {
                    if (!await output.WaitToReadAsync(cancellationToken)) return 0; // shell closed
                    if (!output.TryRead(out var chunk)) return 0;
                    _leftover = chunk;
                }
                catch (ChannelClosedException) { return 0; }
            }

            var n = Math.Min(_leftover.Length, buffer.Length);
            _leftover.Span[..n].CopyTo(buffer.Span);
            _leftover = _leftover[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Synchronous Write + Flush is the reliable way to send PTY input —
            // the base-class async wrappers don't flush ShellStream dependably.
            shell.Write(buffer, offset, count);
            shell.Flush();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array is not null)
                Write(segment.Array, segment.Offset, segment.Count);
            else
                Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() => shell.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
