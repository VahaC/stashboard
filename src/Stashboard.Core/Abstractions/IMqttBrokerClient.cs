namespace Stashboard.Core.Abstractions;

/// <summary>
/// V9.0 — connection + Last-Will parameters for one broker session. Built from the
/// decrypted, DB-backed MQTT settings by the publisher and handed to the transport.
/// </summary>
public sealed record MqttConnectionSettings(
    string Host,
    int Port,
    bool UseTls,
    bool AllowUntrustedTls,
    string? Username,
    string? Password,
    string ClientId,
    /// <summary>Single retained availability topic registered as the MQTT Last Will,
    /// referenced by every discovered entity so they flip to <c>unavailable</c> when
    /// Stashboard stops or the connection drops.</summary>
    string AvailabilityTopic,
    string OnlinePayload,
    string OfflinePayload);

/// <summary>
/// V9.0 — thin abstraction over an MQTT client holding one long-lived broker
/// connection. Wraps the concrete MQTTnet client (registered in Infrastructure) so
/// the publisher's reconcile / lifecycle logic can be unit-tested against a fake
/// without a real broker.
/// </summary>
public interface IMqttBrokerClient : IAsyncDisposable
{
    /// <summary>True while a broker session is established.</summary>
    bool IsConnected { get; }

    /// <summary>Opens a session with the supplied parameters, registering the Last Will.
    /// Throws on failure so the caller can surface "broker unreachable".</summary>
    Task ConnectAsync(MqttConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Publishes one message. <paramref name="retain"/> keeps the broker's last
    /// value so Home Assistant gets it immediately on (re)connect. An empty
    /// <paramref name="payload"/> on a retained topic clears it (removes the entity).</summary>
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default);

    /// <summary>Closes the session cleanly (sends the offline availability message first).</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
