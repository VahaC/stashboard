namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// The entity families the publisher surfaces to Home Assistant. V9.0 shipped the
/// three MVP binary-sensor families; V9.1 adds the derived-signal sensors
/// (<see cref="UpdateCount"/>, <see cref="NodeAlert"/>, <see cref="BackupAge"/>,
/// <see cref="Rollup"/>) — published the same way, just over the <c>sensor</c>
/// component (and a <c>problem</c> binary_sensor for alerts).
/// </summary>
public enum MqttEntityKind
{
    /// <summary>V9.0 — Docker container / Proxmox guest running state (binary_sensor, device_class <c>running</c>).</summary>
    Running,
    /// <summary>V9.0 — Docker image-update available (binary_sensor, device_class <c>update</c>).</summary>
    Update,
    /// <summary>V9.0 — Service health-check online/offline (binary_sensor, device_class <c>connectivity</c>).</summary>
    Health,

    /// <summary>V9.1 — pending-update <em>count</em> for a Docker host, Proxmox node, or
    /// LXC (sensor, numeric, state_class <c>measurement</c>). Complements the V9.0
    /// boolean "update available".</summary>
    UpdateCount,
    /// <summary>V9.1 — the V6.8.1 node-alert verdict for a PVE/PBS node (binary_sensor,
    /// device_class <c>problem</c>): on = warn/crit, off = clear, with the per-category
    /// breakdown + worst severity carried as JSON attributes.</summary>
    NodeAlert,
    /// <summary>V9.1 — age of a guest's most recent vzdump backup (sensor, device_class
    /// <c>timestamp</c>) so HA renders "x days ago" and can alert on a stale backup.</summary>
    BackupAge,
    /// <summary>V9.1 — an estate roll-up summary count on the Stashboard hub device
    /// (sensor, numeric). The concrete metric is named by <see cref="MqttSourceEntity.MetricKey"/>.</summary>
    Rollup,
}

/// <summary>
/// One source signal the publisher wants to surface, emitted by
/// <see cref="IMqttEntityStateProvider"/>. Several entities can share a
/// <see cref="DeviceKey"/> (e.g. a container's running + update + count sensors) so
/// the builder groups them under one Home Assistant device.
/// <para>Which of <see cref="IsOn"/> / <see cref="Number"/> / <see cref="Timestamp"/>
/// carries the state is decided by <see cref="Kind"/>; the unused ones stay
/// <c>null</c>. <see cref="Attributes"/> is only set for <see cref="MqttEntityKind.NodeAlert"/>,
/// <see cref="MetricKey"/> only for <see cref="MqttEntityKind.Rollup"/>.</para>
/// </summary>
/// <param name="Kind">Which entity family this is.</param>
/// <param name="DeviceKey">Stable, prefix-independent key for the real object this
/// entity belongs to (e.g. <c>docker:{connId}:{container}</c>, <c>node:{connId}</c>).
/// Drives the device grouping and the short id hash, so it must be stable across
/// restarts.</param>
/// <param name="DeviceName">Human device name shown in HA (e.g. the container name).</param>
/// <param name="DeviceModel">HA device "model" line (e.g. <c>Docker container</c>).</param>
/// <param name="IsOn">Binary state for the binary-sensor families. <c>null</c> = unknown.</param>
/// <param name="Number">Numeric state for <see cref="MqttEntityKind.UpdateCount"/> /
/// <see cref="MqttEntityKind.Rollup"/>. <c>null</c> = unknown.</param>
/// <param name="Timestamp">Instant for <see cref="MqttEntityKind.BackupAge"/>.
/// <c>null</c> = unknown (no backup yet).</param>
/// <param name="Attributes">JSON attributes for <see cref="MqttEntityKind.NodeAlert"/>
/// (per-category breakdown + worst severity).</param>
/// <param name="MetricKey">For <see cref="MqttEntityKind.Rollup"/> only — the metric's
/// id suffix on the hub device (e.g. <c>containers_running</c>).</param>
public sealed record MqttSourceEntity(
    MqttEntityKind Kind,
    string DeviceKey,
    string DeviceName,
    string DeviceModel,
    bool? IsOn = null,
    double? Number = null,
    DateTimeOffset? Timestamp = null,
    IReadOnlyDictionary<string, object?>? Attributes = null,
    string? MetricKey = null);

/// <summary>V9.0 — a single MQTT message to publish (or, with an empty payload, to clear).</summary>
public sealed record MqttMessage(string Topic, string Payload, bool Retain);

/// <summary>
/// The fully-resolved set of topics + payloads for one source entity, ready for the
/// reconciler to diff against what it last published. V9.1 adds the optional
/// attributes topic used by the node-alert problem sensor.
/// </summary>
public sealed record MqttDesiredEntity(
    string ObjectId,
    string DiscoveryTopic,
    string DiscoveryPayload,
    string StateTopic,
    /// <summary><c>null</c> when the state is unknown — the reconciler publishes
    /// discovery but withholds the state so HA shows "unknown" rather than a guess.</summary>
    string? StatePayload,
    /// <summary>V9.1 — the retained <c>json_attributes_topic</c>, set only for entities
    /// that carry attributes (the node-alert sensor); <c>null</c> otherwise.</summary>
    string? AttributesTopic = null,
    /// <summary>V9.1 — the attributes JSON payload; <c>null</c> when there are none.</summary>
    string? AttributesPayload = null);
