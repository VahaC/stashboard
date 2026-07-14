using System.Net.WebSockets;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V8.6 — adapts a browser <see cref="WebSocket"/> to the
/// <see cref="IProxmoxVncChannel"/> seam so the VNC byte pump can treat both ends
/// (browser and Proxmox) uniformly. The RFB stream is binary in both directions;
/// each received frame is surfaced verbatim and each write goes out as a single
/// binary frame. RFB does its own message framing above the WebSocket layer, so
/// re-framing across the relay is harmless.
/// </summary>
public sealed class WebSocketVncChannel(WebSocket socket) : IProxmoxVncChannel
{
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return result.MessageType == WebSocketMessageType.Close ? 0 : result.Count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    // The browser socket's lifetime is owned by the controller (it `using`s the
    // accepted socket and closes it with a reason), so disposal here is a no-op.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
