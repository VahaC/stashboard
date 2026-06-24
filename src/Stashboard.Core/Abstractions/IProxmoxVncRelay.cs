namespace Stashboard.Core.Abstractions;

/// <summary>
/// V8.6 — the one-time VNC session Proxmox hands back from
/// <c>POST /nodes/{node}/qemu/{vmid}/vncproxy</c> (<c>websocket=1</c>):
/// a short-lived <see cref="Ticket"/> and the internal display <see cref="Port"/>.
/// The ticket does double duty — it both authorises the
/// <c>vncwebsocket</c> upgrade (as the <c>vncticket</c> query parameter, supplied
/// server-side) <em>and</em> is the RFB password the guest's VNC server challenges
/// for, so the browser's noVNC must present it. It is <strong>not</strong> the
/// Proxmox API token: it is an ephemeral, per-session VNC credential, so handing it
/// to the browser keeps the acceptance bar ("the Proxmox token never reaches the
/// browser") intact.
/// </summary>
public sealed record ProxmoxVncProxyTicket(string Ticket, int Port);

/// <summary>
/// V8.6 — a live, byte-duplex channel to a Proxmox <c>vncwebsocket</c>. The relay
/// pumps the raw RFB (VNC) stream through this verbatim: <see cref="ReadAsync"/>
/// drains host→browser bytes (a <c>0</c> read means the peer closed),
/// <see cref="WriteAsync"/> forwards browser→host bytes. Disposing tears down the
/// underlying socket. Mirrors the V6.6 console's <see cref="IHostShellChannel"/>
/// seam so the WebSocket ↔ byte pump above it stays testable without a live host.
/// </summary>
public interface IProxmoxVncChannel : IAsyncDisposable
{
    /// <summary>Reads the next chunk of host→browser RFB bytes into
    /// <paramref name="buffer"/>. Returns the byte count, or <c>0</c> when the
    /// Proxmox side closed the socket.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Forwards browser→host RFB bytes to Proxmox verbatim.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>
/// V8.6 — the transport seam behind the browser VM console. A VM has no
/// <c>pct exec</c> and no guaranteed SSH / guest-agent, so the V6.6 SSH-PTY
/// transport can't be reused; the only universal VM console is Proxmox's built-in
/// VNC. This relay owns the two Proxmox calls — mint the VNC session
/// (<see cref="OpenVncProxyAsync"/>), then open the raw RFB websocket
/// (<see cref="ConnectAsync"/>) — keeping the API token server-side (TLS to the
/// host, token in the <c>Authorization</c> header). The byte pump above it
/// (<c>ProxmoxVncSession</c>) is orchestrated against this interface so it stays
/// testable with in-memory fakes, exactly like the V6.6 console.
/// </summary>
public interface IProxmoxVncRelay
{
    /// <summary>
    /// Calls <c>POST /nodes/{node}/qemu/{vmid}/vncproxy</c> (<c>websocket=1</c>)
    /// and returns the one-time VNC ticket + display port. Throws when the host
    /// refuses (e.g. the VM is stopped, or the token lacks <c>VM.Console</c>) so the
    /// caller can surface a clean "console unavailable on this host" message rather
    /// than opening a broken canvas.
    /// </summary>
    Task<ProxmoxVncProxyTicket> OpenVncProxyAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the Proxmox <c>vncwebsocket</c> for a VNC session previously minted by
    /// <see cref="OpenVncProxyAsync"/> and returns a duplex byte channel carrying
    /// the raw RFB stream. Authenticates with the API token in the
    /// <c>Authorization</c> header (kept server-side) plus the
    /// <paramref name="vncProxy"/> ticket as the <c>vncticket</c> query parameter.
    /// Throws on a failed upgrade — a PVE version that refuses token-auth
    /// <c>vncwebsocket</c> relay surfaces as a clean error here rather than a hung
    /// socket.
    /// </summary>
    Task<IProxmoxVncChannel> ConnectAsync(
        ProxmoxConnectionProfile profile, int vmId, ProxmoxVncProxyTicket vncProxy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// V8.6 — a clean, user-facing failure to open the Proxmox VNC relay (a refused
/// token-auth <c>vncwebsocket</c> upgrade, a stopped VM, a console-less VM). The
/// controller turns this into the documented "console unavailable on this host"
/// message rather than letting a broken canvas hang.
/// </summary>
public sealed class ProxmoxVncRelayException(string message, Exception? inner = null)
    : Exception(message, inner);
