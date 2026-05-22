using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V3.3 — production <see cref="IDockerLogStreamer"/>. Talks to the Docker
/// Engine's <c>GET /containers/{id}/logs?follow=…&amp;stdout=…&amp;stderr=…</c>
/// endpoint via Docker.DotNet's <see cref="MultiplexedStream"/>, demuxes the
/// stdcopy header frames into stdout / stderr channels, splits the byte
/// stream into lines, and parses out the per-line RFC3339Nano timestamp the
/// daemon prepends when <c>Timestamps = true</c>.
/// </summary>
/// <remarks>
/// Stdcopy frame layout (when the container has <c>tty = false</c>):
/// <code>
///   ┌─────────────┬───────────────┬───────────────────┐
///   │ stream (1B) │ padding (3B)  │ payload size (4B) │  ← 8-byte header
///   ├─────────────┴───────────────┴───────────────────┤
///   │              payload bytes (UTF-8)              │
///   └─────────────────────────────────────────────────┘
/// </code>
/// Docker.DotNet's <see cref="MultiplexedStream.ReadOutputAsync"/> does the
/// header parsing for us and surfaces a <c>Target</c> (stdin / stdout /
/// stderr) plus the payload slice on each read. For tty-attached containers
/// the stream is *not* multiplexed — every byte is plain stdout — and the
/// same <c>ReadOutputAsync</c> path reports <c>Target = Standard</c> with
/// the raw payload, which we map to <see cref="DockerLogStreamChannel.Stdout"/>.
/// </remarks>
public sealed class DockerLogStreamer(
    IDockerClientFactory dockerClientFactory,
    ILogger<DockerLogStreamer> logger) : IDockerLogStreamer
{
    private const int ReadBufferSize = 16 * 1024;

    public async Task<DockerLogStreamResult> StreamLogsAsync(
        DockerHostTransport transport,
        string containerName,
        DockerLogStreamRequest request,
        Func<DockerLogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return new DockerLogStreamResult(DockerHostStatus.ContainerNotFound, "Container name is required.");
        if (!request.IncludeStdout && !request.IncludeStderr)
            return new DockerLogStreamResult(DockerHostStatus.Ok, null);

        IDockerClient client;
        try
        {
            client = dockerClientFactory.Create(transport.HostType, transport.HostUrl, transport.Tls, transport.Ssh);
        }
        catch (NotSupportedException ex)
        {
            return new DockerLogStreamResult(DockerHostStatus.UnsupportedHostType, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed for Docker host: {Host}", transport.Ssh?.Host);
            return new DockerLogStreamResult(DockerHostStatus.HostUnreachable, $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            // The daemon multiplexes stdout / stderr in stdcopy frames *only*
            // when the container was launched without a TTY. We have to ask
            // the daemon up-front so we can pass the correct `tty` flag to
            // GetContainerLogsAsync — passing the wrong value either turns
            // 8-byte stdcopy headers into garbage payload bytes or splits a
            // plain stream every 8 bytes.
            bool tty;
            try
            {
                var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
                tty = inspect.Config?.Tty ?? false;
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerLogStreamResult(DockerHostStatus.ContainerNotFound,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new DockerLogStreamResult(DockerHostStatus.ContainerNotFound,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable when starting log stream for {Container}", containerName);
                return new DockerLogStreamResult(DockerHostStatus.HostUnreachable, ex.Message);
            }

            var parameters = new ContainerLogsParameters
            {
                Follow = request.Follow,
                ShowStdout = request.IncludeStdout,
                ShowStderr = request.IncludeStderr,
                Timestamps = request.Timestamps,
                Tail = request.Tail is int n && n > 0 ? n.ToString(CultureInfo.InvariantCulture) : "all",
                Since = request.Since is { } since
                    ? since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                    : null,
            };

            MultiplexedStream stream;
            try
            {
                stream = await client.Containers.GetContainerLogsAsync(
                    containerName, tty, parameters, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerLogStreamResult(DockerHostStatus.ContainerNotFound,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable when streaming logs for {Container}", containerName);
                return new DockerLogStreamResult(DockerHostStatus.HostUnreachable, ex.Message);
            }

            using (stream)
            {
                try
                {
                    await PumpStreamAsync(stream, request.Timestamps, onLine, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Graceful disconnect — the consumer (browser tab) closed
                    // the stream. Nothing to report.
                }
                catch (IOException ex)
                {
                    // The daemon closed the connection mid-stream (container
                    // gone, daemon restart). Treat as a normal end-of-stream;
                    // the caller has already received whatever lines arrived.
                    logger.LogDebug(ex, "Docker log stream for {Container} ended unexpectedly", containerName);
                }
            }

            return DockerLogStreamResult.Ok;
        }
    }

    /// <summary>
    /// Reads stdcopy frames from <paramref name="stream"/> until cancelled or
    /// EOF, accumulates per-channel partial-line buffers, and emits one
    /// <see cref="DockerLogLine"/> per newline-terminated line. The two
    /// buffers are kept independent because stdout and stderr lines can be
    /// interleaved at arbitrary byte boundaries on the wire — flushing the
    /// stderr buffer when an stdout frame arrives would split lines whose
    /// trailing newline is still pending.
    /// </summary>
    private static async Task PumpStreamAsync(
        MultiplexedStream stream,
        bool timestampsEnabled,
        Func<DockerLogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var stdoutPartial = new MemoryStream();
        var stderrPartial = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                if (read.EOF) break;
                if (read.Count <= 0) continue;

                var channel = read.Target switch
                {
                    MultiplexedStream.TargetStream.StandardError => DockerLogStreamChannel.Stderr,
                    _ => DockerLogStreamChannel.Stdout,
                };
                var partial = channel == DockerLogStreamChannel.Stderr ? stderrPartial : stdoutPartial;

                await EmitLinesAsync(
                    partial, buffer.AsMemory(0, read.Count), channel, timestampsEnabled, onLine, cancellationToken);
            }

            // Flush trailing partial lines (logs that never ended in '\n'
            // before the stream closed). Docker normally always emits a
            // newline at end-of-stream, but apps that flush stdout with
            // `print(..., end='')` are common enough to handle.
            await FlushPartialAsync(stdoutPartial, DockerLogStreamChannel.Stdout, timestampsEnabled, onLine, cancellationToken);
            await FlushPartialAsync(stderrPartial, DockerLogStreamChannel.Stderr, timestampsEnabled, onLine, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stdoutPartial.Dispose();
            stderrPartial.Dispose();
        }
    }

    private static async Task EmitLinesAsync(
        MemoryStream partial,
        ReadOnlyMemory<byte> chunk,
        DockerLogStreamChannel channel,
        bool timestampsEnabled,
        Func<DockerLogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken)
    {
        // Collect complete lines first (no awaits while we hold a Span over
        // the rented buffer), then emit them — async callback must not span
        // a Span<byte> lifetime.
        var lines = new List<byte[]>();
        var start = 0;
        var span = chunk.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)'\n') continue;
            partial.Write(span[start..i]);
            lines.Add(partial.ToArray());
            partial.SetLength(0);
            start = i + 1;
        }
        if (start < span.Length)
            partial.Write(span[start..]);

        foreach (var lineBytes in lines)
        {
            var line = BuildLine(lineBytes, channel, timestampsEnabled);
            await onLine(line, cancellationToken);
        }
    }

    private static async Task FlushPartialAsync(
        MemoryStream partial,
        DockerLogStreamChannel channel,
        bool timestampsEnabled,
        Func<DockerLogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken)
    {
        if (partial.Length == 0) return;
        var lineBytes = partial.ToArray();
        partial.SetLength(0);
        var line = BuildLine(lineBytes, channel, timestampsEnabled);
        await onLine(line, cancellationToken);
    }

    /// <summary>
    /// Decodes <paramref name="lineBytes"/> as UTF-8 (with replacement for
    /// invalid sequences — log lines occasionally contain stray bytes from
    /// binary blobs), strips a trailing '\r' if present (so CRLF logs on
    /// Windows containers render correctly), and parses out the leading
    /// RFC3339Nano timestamp the daemon prepends when <c>Timestamps = true</c>.
    /// </summary>
    internal static DockerLogLine BuildLine(byte[] lineBytes, DockerLogStreamChannel channel, bool timestampsEnabled)
    {
        var text = Encoding.UTF8.GetString(lineBytes);
        if (text.Length > 0 && text[^1] == '\r')
            text = text[..^1];

        DateTime? timestampUtc = null;
        if (timestampsEnabled)
        {
            var spaceIndex = text.IndexOf(' ');
            if (spaceIndex > 0 && TryParseRfc3339Nano(text.AsSpan(0, spaceIndex), out var parsed))
            {
                timestampUtc = parsed;
                text = text[(spaceIndex + 1)..];
            }
        }

        return new DockerLogLine(channel, timestampUtc, text);
    }

    private static bool IsSshConnectFailure(Exception ex) =>
        ex is SshException or System.Net.Sockets.SocketException;

    /// <summary>
    /// Parses Docker's RFC3339Nano log prefix (e.g.
    /// <c>2026-05-19T18:32:01.123456789Z</c>). Falls back to <see cref="DateTimeOffset.TryParse"/>
    /// after truncating excess fractional digits — .NET's <c>DateTime</c>
    /// stores at most 100ns ticks (7 fractional digits), so Docker's full
    /// 9-digit nanosecond precision would otherwise overflow the parser.
    /// </summary>
    private static bool TryParseRfc3339Nano(ReadOnlySpan<char> input, out DateTime utc)
    {
        utc = default;
        if (input.IsEmpty) return false;

        // Strip sub-100ns digits ('FFFFFFFF…' beyond 7) before handing off
        // to TryParse. This is safe to do byte-by-byte in the fractional
        // segment between '.' and the timezone marker.
        Span<char> scratch = stackalloc char[input.Length];
        var dot = input.IndexOf('.');
        if (dot < 0)
        {
            input.CopyTo(scratch);
            scratch = scratch[..input.Length];
        }
        else
        {
            // Locate the end of the fractional segment (either 'Z', '+', '-' offset
            // marker, or the end of the span if Docker omits the timezone).
            var fracStart = dot + 1;
            var tzStart = fracStart;
            while (tzStart < input.Length && IsDigit(input[tzStart])) tzStart++;
            var fracLen = tzStart - fracStart;
            var keep = Math.Min(fracLen, 7);

            input[..fracStart].CopyTo(scratch);
            input.Slice(fracStart, keep).CopyTo(scratch[fracStart..]);
            input[tzStart..].CopyTo(scratch[(fracStart + keep)..]);
            scratch = scratch[..(fracStart + keep + (input.Length - tzStart))];
        }

        if (DateTimeOffset.TryParse(
                scratch,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }
        return false;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
