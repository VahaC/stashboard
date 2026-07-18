namespace Stashboard.Api.Services.Mqtt;

/// <summary>V9.0 — the three MVP binary-sensor families published to Home Assistant.</summary>
public enum MqttEntityKind
{
    /// <summary>Docker container / Proxmox guest running state (device_class <c>running</c>).</summary>
    Running,
    /// <summary>Docker image-update available (device_class <c>update</c>).</summary>
    Update,
    /// <summary>Service health-check online/offline (device_class <c>connectivity</c>).</summary>
    Health,
}

/// <summary>
/// V9.0 — one source signal the publisher wants to surface, emitted by
/// <see cref="IMqttEntityStateProvider"/>. Several entities can share a
/// <see cref="DeviceKey"/> (e.g. a container's running + update sensors) so the
/// builder groups them under one Home Assistant device.
/// </summary>
/// <param name="Kind">Which binary-sensor family this is.</param>
/// <param name="DeviceKey">Stable, prefix-independent key for the real object this
/// entity belongs to (e.g. <c>docker:{connId}:{container}</c>). Drives the device
/// grouping and the short id hash, so it must be stable across restarts.</param>
/// <param name="DeviceName">Human device name shown in HA (e.g. the container name).</param>
/// <param name="DeviceModel">HA device "model" line (e.g. <c>Docker container</c>).</param>
/// <param name="IsOn">Current state. <c>null</c> = unknown (no state published yet).</param>
public sealed record MqttSourceEntity(
    MqttEntityKind Kind,
    string DeviceKey,
    string DeviceName,
    string DeviceModel,
    bool? IsOn);

/// <summary>V9.0 — a single MQTT message to publish (or, with an empty payload, to clear).</summary>
public sealed record MqttMessage(string Topic, string Payload, bool Retain);

/// <summary>
/// V9.0 — the fully-resolved set of topics + payloads for one source entity, ready
/// for the reconciler to diff against what it last published.
/// </summary>
public sealed record MqttDesiredEntity(
    string ObjectId,
    string DiscoveryTopic,
    string DiscoveryPayload,
    string StateTopic,
    /// <summary><c>null</c> when the state is unknown — the reconciler publishes
    /// discovery but withholds the state so HA shows "unknown" rather than a guess.</summary>
    string? StatePayload);
