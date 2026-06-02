using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V5.7 — production <see cref="IContainerExecConnector"/>. Creates a TTY exec
/// instance on the target container (<c>ExecCreateContainerAsync</c>) and
/// upgrades it to a hijacked bidirectional stream
/// (<c>StartAndAttachContainerExecAsync</c>), exposing the result as the same
/// duplex <see cref="IHostShellChannel"/> the V5.3 host terminal uses so the
/// WebSocket ↔ byte pump is shared. Resolves the daemon connection through the
/// shared <see cref="IDockerClientFactory"/>, so exec works for every host type
/// (local socket, TCP+TLS, SSH tunnel) without new transport plumbing.
/// </summary>
public sealed class DockerContainerExecConnector(
    IDockerClientFactory dockerClientFactory,
    ILogger<DockerContainerExecConnector> logger) : IContainerExecConnector
{
    public async Task<IHostShellChannel> ConnectAsync(
        DockerHostTransport transport,
        ContainerExecRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // factory.Create() may block on an SSH handshake — keep it off the
        // request thread (mirrors the host-shell connector's Task.Run wrap).
        var client = await Task.Run(
            () => dockerClientFactory.Create(transport.HostType, transport.HostUrl, transport.Tls, transport.Ssh),
            cancellationToken);
        try
        {
            var exec = await client.Exec.ExecCreateContainerAsync(
                request.ContainerName,
                new ContainerExecCreateParameters
                {
                    AttachStdin = true,
                    AttachStdout = true,
                    AttachStderr = true,
                    Tty = true,
                    Cmd = request.Command.ToList(),
                },
                cancellationToken);

            var stream = await client.Exec.StartAndAttachContainerExecAsync(
                exec.ID, tty: true, cancellationToken);

            return new ContainerExecChannel(client, stream, exec.ID, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps a hijacked exec <see cref="MultiplexedStream"/> as a duplex
    /// <see cref="IHostShellChannel"/>. <see cref="Completion"/> fires when the
    /// exec'd process exits (the stream EOFs); <see cref="TryResize"/> calls the
    /// daemon's exec-resize endpoint for real. Disposing tears down the stream
    /// and the owning Docker client (which, for an SSH connection, also closes
    /// the tunnel).
    /// </summary>
    private sealed class ContainerExecChannel : IHostShellChannel
    {
        private readonly IDockerClient _client;
        private readonly MultiplexedStream _mux;
        private readonly string _execId;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ExecDuplexStream _stream;
        private int _disposed;

        public Stream Stream => _stream;
        public Task Completion => _completion.Task;

        public ContainerExecChannel(IDockerClient client, MultiplexedStream mux, string execId, ILogger logger)
        {
            _client = client;
            _mux = mux;
            _execId = execId;
            _logger = logger;
            _stream = new ExecDuplexStream(mux, () => _completion.TrySetResult());
        }

        public bool TryResize(uint columns, uint rows)
        {
            // Fire-and-forget — resize is advisory and the byte pump must not
            // block on it. Failures (process already gone) are swallowed.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.Exec.ResizeContainerExecTtyAsync(
                        _execId,
                        new ContainerResizeParameters { Height = rows, Width = columns });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Resizing container exec {ExecId} failed.", _execId);
                }
            });
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _completion.TrySetResult();
            try { _mux.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing exec stream failed."); }
            try { _client.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing exec Docker client failed."); }
        }
    }

    /// <summary>
    /// Duplex adapter over an exec <see cref="MultiplexedStream"/>: reads drain
    /// the container's terminal output (a single TTY stream — every byte is
    /// stdout), writes forward keystrokes to the container's stdin. Signals the
    /// owning channel's completion when the daemon reports EOF.
    /// </summary>
    private sealed class ExecDuplexStream(MultiplexedStream mux, Action onEof) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // ReadOutputAsync needs a backing array; rent one when the caller's
            // buffer isn't array-backed (the byte-pump's buffer always is).
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg) && seg.Array is not null)
            {
                var result = await mux.ReadOutputAsync(seg.Array, seg.Offset, seg.Count, cancellationToken);
                if (result.EOF) { onEof(); return 0; }
                return result.Count;
            }

            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var result = await mux.ReadOutputAsync(rented, 0, buffer.Length, cancellationToken);
                if (result.EOF) { onEof(); return 0; }
                rented.AsMemory(0, result.Count).CopyTo(buffer);
                return result.Count;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg) && seg.Array is not null)
                await mux.WriteAsync(seg.Array, seg.Offset, seg.Count, cancellationToken);
            else
            {
                var array = buffer.ToArray();
                await mux.WriteAsync(array, 0, array.Length, cancellationToken);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
