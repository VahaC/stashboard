using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.HostShell;

/// <summary>V5.3 — outcome of a finished host-terminal session.</summary>
public sealed record HostShellSessionResult(
    HostShellSessionEndReason EndReason,
    long BytesFromClient,
    long BytesToClient,
    string? Error);

/// <summary>
/// V5.3 — the heart of the host terminal: pumps bytes between the connected host
/// PTY (<see cref="IHostShellChannel"/>) and the browser
/// (<see cref="IShellClientTransport"/>) until either side closes or the
/// inactivity timeout fires. Counts bytes in both directions, dispatches resize
/// requests, and reports why the session ended. Deliberately transport-agnostic
/// (no <c>WebSocket</c> / <c>SshClient</c> here) so it can be exercised against
/// in-memory fakes.
/// </summary>
public static class HostShellSession
{
    private const int ReadBufferSize = 16 * 1024;

    public static async Task<HostShellSessionResult> RunAsync(
        IHostShellChannel host,
        IShellClientTransport client,
        TimeSpan idleTimeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(client);

        long bytesFromClient = 0;
        long bytesToClient = 0;
        long lastActivity = Stopwatch.GetTimestamp();

        // Default: if nothing else claims a terminal reason (e.g. the external
        // token cancels because the app is stopping), it was a server close.
        var endReason = HostShellSessionEndReason.ClosedByServer;
        string? error = null;
        var reasonClaimed = 0;

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = sessionCts.Token;

        void Touch() => Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp());

        // First terminal reason wins; cancel so the sibling pumps unwind.
        void Claim(HostShellSessionEndReason reason, string? detail = null)
        {
            if (Interlocked.Exchange(ref reasonClaimed, 1) == 0)
            {
                endReason = reason;
                error = detail;
            }
            sessionCts.Cancel();
        }

        async Task ClientToHostAsync()
        {
            try
            {
                while (true)
                {
                    var message = await client.ReceiveAsync(token);
                    switch (message)
                    {
                        case ShellClientMessage.Input input:
                            Interlocked.Add(ref bytesFromClient, input.Data.Length);
                            Touch();
                            await host.Stream.WriteAsync(input.Data, token);
                            await host.Stream.FlushAsync(token);
                            break;
                        case ShellClientMessage.Resize resize:
                            Touch();
                            host.TryResize(resize.Columns, resize.Rows);
                            break;
                        case ShellClientMessage.Closed:
                            Claim(HostShellSessionEndReason.ClosedByClient);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) { /* sibling ended the session */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Host shell client→host pump ended with an error.");
                Claim(HostShellSessionEndReason.Error, ex.Message);
            }
            finally { sessionCts.Cancel(); }
        }

        async Task HostToClientAsync()
        {
            var buffer = new byte[ReadBufferSize];
            try
            {
                while (true)
                {
                    int read = await host.Stream.ReadAsync(buffer, token);
                    if (read <= 0)
                    {
                        Claim(HostShellSessionEndReason.RemoteClosed);
                        return;
                    }
                    Interlocked.Add(ref bytesToClient, read);
                    Touch();
                    await client.SendAsync(buffer.AsMemory(0, read), token);
                }
            }
            catch (OperationCanceledException) { /* sibling ended the session */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Host shell host→client pump ended with an error.");
                Claim(HostShellSessionEndReason.Error, ex.Message);
            }
            finally { sessionCts.Cancel(); }
        }

        async Task IdleWatcherAsync()
        {
            if (idleTimeout <= TimeSpan.Zero) return; // disabled
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity));
                    if (elapsed >= idleTimeout)
                    {
                        Claim(HostShellSessionEndReason.IdleTimeout);
                        return;
                    }
                    await Task.Delay(idleTimeout - elapsed, token);
                }
            }
            catch (OperationCanceledException) { /* session ended first */ }
        }

        await Task.WhenAll(ClientToHostAsync(), HostToClientAsync(), IdleWatcherAsync());

        // Only when the remote shell exited do we wait for the channel to finish
        // tearing down (bounded, best-effort) — for a client/idle/server close the
        // remote is still up and the caller disposes the channel, so waiting would
        // just stall.
        if (endReason == HostShellSessionEndReason.RemoteClosed)
        {
            try { await host.Completion.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch { /* completion is advisory */ }
        }

        return new HostShellSessionResult(
            endReason,
            Interlocked.Read(ref bytesFromClient),
            Interlocked.Read(ref bytesToClient),
            error);
    }
}
