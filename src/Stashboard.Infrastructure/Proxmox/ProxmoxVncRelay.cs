using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V8.6 — production <see cref="IProxmoxVncRelay"/>: the server-side bridge to a
/// Proxmox VM's built-in VNC console. Two steps, both authenticated with the API
/// token (kept server-side, never on the wire to the browser):
/// <list type="number">
///   <item><see cref="OpenVncProxyAsync"/> — <c>POST …/qemu/{vmid}/vncproxy</c>
///   (<c>websocket=1</c>) mints a one-time VNC ticket + display port.</item>
///   <item><see cref="ConnectAsync"/> — opens the raw RFB <c>vncwebsocket</c> with
///   a <see cref="ClientWebSocket"/>, presenting the token in the
///   <c>Authorization</c> header and the ticket as the <c>vncticket</c> query
///   parameter.</item>
/// </list>
/// Reuses <see cref="ProxmoxApiClient"/>'s TLS posture (the same per-connection
/// <c>SkipTlsVerify</c> for the self-signed certs homelab Proxmox installs ship
/// with) and its <c>PVEAPIToken</c> auth header. The byte pump that drives RFB
/// both ways lives above this in the API layer.
/// </summary>
internal sealed class ProxmoxVncRelay(
    IHttpClientFactory httpClientFactory, ILogger<ProxmoxVncRelay> logger) : IProxmoxVncRelay
{
    /// <summary>Bounds the websocket upgrade so a reverse proxy that swallows the
    /// handshake (or a PVE version that refuses token-auth relay) surfaces as a
    /// clean error rather than a hung socket.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<ProxmoxVncProxyTicket> OpenVncProxyAsync(
        ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(
            profile.SkipTlsVerify ? ProxmoxApiClient.InsecureHttpClientName : ProxmoxApiClient.HttpClientName);

        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/api2/json/nodes/{profile.NodeName}/qemu/{vmId}/vncproxy")
        {
            // websocket=1 makes Proxmox set up the vncwebsocket-compatible proxy;
            // generate-password is intentionally left default so the ticket itself
            // is the RFB credential (what the Proxmox web UI does too).
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("websocket", "1")]),
        };
        request.Headers.Authorization = ProxmoxApiClient.BuildAuthHeader(profile);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
            throw new HttpRequestException($"Proxmox returned {(int)response.StatusCode}: {detail}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new HttpRequestException("Proxmox vncproxy returned no session data.");

        var ticket = data.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        var port = ReadInt(data, "port");
        if (string.IsNullOrEmpty(ticket) || port is null)
            throw new HttpRequestException("Proxmox vncproxy returned an incomplete session (missing ticket / port).");

        return new ProxmoxVncProxyTicket(ticket, port.Value);
    }

    public async Task<IProxmoxVncChannel> ConnectAsync(
        ProxmoxConnectionProfile profile, int vmId, ProxmoxVncProxyTicket vncProxy,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = profile.ApiBaseUrl.TrimEnd('/');
        // The vncwebsocket lives on the same host:port as the REST API (8006) — the
        // `port` from vncproxy is the internal display port and rides as a query
        // parameter, not the socket's TCP port. Swap http(s) → ws(s).
        var wsBase = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + baseUrl["https://".Length..]
            : baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "ws://" + baseUrl["http://".Length..]
                : baseUrl;
        var uri = new Uri(
            $"{wsBase}/api2/json/nodes/{profile.NodeName}/qemu/{vmId}/vncwebsocket"
            + $"?port={vncProxy.Port}&vncticket={Uri.EscapeDataString(vncProxy.Ticket)}");

        var socket = new ClientWebSocket();
        // noVNC negotiates the "binary" subprotocol; match it so RFB bytes flow raw
        // (the legacy "base64" framing would force a transform — we want verbatim).
        socket.Options.AddSubProtocol("binary");
        // The API token stays here, server-side — it never reaches the browser.
        socket.Options.SetRequestHeader("Authorization", ProxmoxApiClient.BuildAuthHeader(profile).ToString());
        if (profile.SkipTlsVerify)
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(uri, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            // Log the full underlying failure server-side so an operator can see the
            // exact reason (e.g. the HTTP status Proxmox returned on the upgrade).
            logger.LogWarning(ex,
                "VM console vncwebsocket upgrade failed for VM {VmId} on node {Node} ({BaseUrl}).",
                vmId, profile.NodeName, profile.ApiBaseUrl);
            // A failed upgrade is the documented feasibility fallback: surface a clean,
            // actionable message (kept short so it survives the WebSocket close-reason
            // 123-byte cap) instead of a half-open socket.
            throw new ProxmoxVncRelayException(DescribeUpgradeFailure(ex), ex);
        }

        return new ClientWebSocketVncChannel(socket);
    }

    /// <summary>Turns the raw <see cref="ClientWebSocket.ConnectAsync"/> failure into
    /// a short, user-facing reason. The vncproxy POST has already succeeded by the
    /// time we get here (so the token <em>can</em> reach the host and has
    /// <c>VM.Console</c>), which makes a 401 specifically the "token auth not accepted
    /// on the vncwebsocket" PVE caveat rather than a bad token.</summary>
    private static string DescribeUpgradeFailure(Exception ex)
    {
        // Keep each message ≤120 chars — the controller relays it as the WebSocket
        // close reason, which the protocol caps at 123 bytes (the full detail is in
        // the server log above).
        if (ex is OperationCanceledException)
            return "VNC websocket upgrade timed out — a reverse proxy may not be forwarding WebSocket upgrades to Proxmox.";

        var status = ExtractHttpStatus(ex);
        return status switch
        {
            401 => "Proxmox refused the VNC websocket: 401 — this PVE version rejects API-token auth for the console.",
            403 => "Proxmox denied the VNC websocket: 403 — the API token needs the VM.Console privilege on this VM.",
            { } code => $"Proxmox returned HTTP {code} on the VNC websocket upgrade.",
            _ => $"VNC websocket could not be opened: {ex.Message}",
        };
    }

    /// <summary>Pulls the HTTP status code out of a failed-upgrade exception. The
    /// managed <see cref="ClientWebSocket"/> reports it only in the message text
    /// ("The server returned status code '401' when status code '101' was
    /// expected."), so parse it out.</summary>
    private static int? ExtractHttpStatus(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = Regex.Match(e.Message, @"status code '(\d{3})'");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var code)) return code;
        }
        return null;
    }

    // ProxmoxVncRelayException lives in Stashboard.Core.Abstractions alongside the
    // IProxmoxVncRelay seam so the API controller can catch it without taking an
    // Infrastructure dependency.

    /// <summary>Reads an integer Proxmox may encode as a JSON number or string.</summary>
    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>Wraps a connected <see cref="ClientWebSocket"/> as an
    /// <see cref="IProxmoxVncChannel"/>. Each received frame is forwarded as-is; the
    /// pump re-frames it onto the browser socket, which is harmless because RFB does
    /// its own message framing above the WebSocket layer.</summary>
    private sealed class ClientWebSocketVncChannel(ClientWebSocket socket) : IProxmoxVncChannel
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

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* best-effort */ }
            finally { socket.Dispose(); }
        }
    }
}
