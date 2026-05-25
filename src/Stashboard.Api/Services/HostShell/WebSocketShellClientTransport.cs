using System.Net.WebSockets;
using System.Text.Json;

namespace Stashboard.Api.Services.HostShell;

/// <summary>
/// V5.3 — adapts a browser <see cref="WebSocket"/> to the
/// <see cref="IShellClientTransport"/> seam. Binary frames carry raw terminal
/// input; text frames carry JSON control messages (<c>{"type":"resize",
/// "cols":120,"rows":40}</c>). Output to the browser is always sent as binary.
/// </summary>
public sealed class WebSocketShellClientTransport(WebSocket socket) : IShellClientTransport
{
    /// <summary>Hard cap on a single inbound message so a hostile / buggy client
    /// can't make us buffer unbounded memory. Keystrokes and pastes are tiny;
    /// 1 MiB is generous.</summary>
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly byte[] _receiveBuffer = new byte[16 * 1024];

    public async ValueTask<ShellClientMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(_receiveBuffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return new ShellClientMessage.Closed();

                    ms.Write(_receiveBuffer, 0, result.Count);
                    if (ms.Length > MaxMessageBytes)
                        return new ShellClientMessage.Closed();
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) { throw; }
            catch (WebSocketException)
            {
                // Abrupt drop (no close handshake) — treat as a client close.
                return new ShellClientMessage.Closed();
            }

            if (result.MessageType == WebSocketMessageType.Binary)
                return new ShellClientMessage.Input(ms.ToArray());

            // Text frame — a control message. Parse resize; ignore anything else
            // and loop for the next frame so unknown control noise can't stall.
            if (TryParseResize(ms.ToArray(), out var resize))
                return resize;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    public async ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            // WebSocket close reasons are capped at 123 bytes by the protocol.
            var trimmed = reason.Length > 120 ? reason[..120] : reason;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, trimmed, cancellationToken);
        }
        catch (WebSocketException) { /* peer already gone */ }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static bool TryParseResize(byte[] utf8, out ShellClientMessage.Resize resize)
    {
        resize = new ShellClientMessage.Resize(0, 0);
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "resize") return false;
            if (!root.TryGetProperty("cols", out var cols) || !root.TryGetProperty("rows", out var rows)) return false;

            var c = cols.GetUInt32Safe();
            var r = rows.GetUInt32Safe();
            if (c == 0 || r == 0) return false;

            resize = new ShellClientMessage.Resize(c, r);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

file static class JsonElementExtensions
{
    public static uint GetUInt32Safe(this JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var n)) return n;
        return 0;
    }
}
