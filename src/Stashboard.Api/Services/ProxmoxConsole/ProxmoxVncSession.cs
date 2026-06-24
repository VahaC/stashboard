using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>V8.6 — outcome of a finished VM-console (VNC relay) session. Reuses
/// the shared <see cref="HostShellSessionEndReason"/> so VM and LXC console rows
/// land in the same audit table with the same end-reason vocabulary.</summary>
public sealed record ProxmoxVncSessionResult(
    HostShellSessionEndReason EndReason,
    long BytesFromClient,
    long BytesToClient,
    string? Error);

/// <summary>
/// V8.6 — the heart of the browser VM console: pumps raw RFB (VNC) bytes between
/// the browser (<see cref="IProxmoxVncChannel"/>) and the Proxmox
/// <c>vncwebsocket</c> (also <see cref="IProxmoxVncChannel"/>) <strong>verbatim,
/// byte-for-byte</strong> until either side closes or the inactivity timeout fires.
/// Counts bytes in both directions and reports why the session ended. This is the
/// VNC analogue of the V6.6 console's <see cref="HostShellSession"/>; it is simpler
/// because the VNC transport carries no control frames (no resize / command — RFB
/// negotiates geometry itself), so every frame in both directions is opaque payload.
/// Deliberately transport-agnostic (no <c>WebSocket</c> here) so it can be exercised
/// against in-memory fakes.
/// </summary>
public static class ProxmoxVncSession
{
    private const int ReadBufferSize = 16 * 1024;

    public static async Task<ProxmoxVncSessionResult> RunAsync(
        IProxmoxVncChannel client,
        IProxmoxVncChannel host,
        TimeSpan idleTimeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(host);

        long bytesFromClient = 0;
        long bytesToClient = 0;
        long lastActivity = Stopwatch.GetTimestamp();

        // Default: if nothing else claims a terminal reason (e.g. the external token
        // cancels because the app is stopping), it was a server close.
        var endReason = HostShellSessionEndReason.ClosedByServer;
        string? error = null;
        var reasonClaimed = 0;

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = sessionCts.Token;

        void Touch() => Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp());

        // First terminal reason wins; cancel so the sibling pump unwinds.
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
            var buffer = new byte[ReadBufferSize];
            try
            {
                while (true)
                {
                    int read = await client.ReadAsync(buffer, token);
                    if (read <= 0)
                    {
                        Claim(HostShellSessionEndReason.ClosedByClient);
                        return;
                    }
                    Interlocked.Add(ref bytesFromClient, read);
                    Touch();
                    await host.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            catch (OperationCanceledException) { /* sibling ended the session */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "VM console client→host pump ended with an error.");
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
                    int read = await host.ReadAsync(buffer, token);
                    if (read <= 0)
                    {
                        Claim(HostShellSessionEndReason.RemoteClosed);
                        return;
                    }
                    Interlocked.Add(ref bytesToClient, read);
                    Touch();
                    await client.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            catch (OperationCanceledException) { /* sibling ended the session */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "VM console host→client pump ended with an error.");
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

        return new ProxmoxVncSessionResult(
            endReason,
            Interlocked.Read(ref bytesFromClient),
            Interlocked.Read(ref bytesToClient),
            error);
    }
}
