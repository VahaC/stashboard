using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 / V9.1 — pure builder that turns the source signals into Home Assistant
/// MQTT-Discovery config payloads + retained state topics. No I/O, so the topic
/// shape, id prefixing, device grouping and availability wiring are all unit-tested
/// without a broker.
/// </summary>
/// <remarks>
/// Id scheme (all start with the configured entity prefix, e.g. <c>stashboard</c>):
/// <list type="bullet">
/// <item><b>node id</b> = <c>{prefix}_{nameSlug}_{shortHash}</c> — per-source, the hash
/// keeping the discovery topic + object_id globally unique without an ugly guid.</item>
/// <item><b>device.identifiers</b> = the per-source <c>nodeId</c> — keyed by the real
/// object's DeviceKey, so a Docker container and a Proxmox LXC that share a name stay
/// distinct devices. Sensors group only when the provider hands them the same DeviceKey
/// (a container's running + update + update-count, a node's alert + update-count, …).</item>
/// <item><b>object_id / unique_id</b> = <c>{nodeId}_{suffix}</c>.</item>
/// <item><b>name</b> = <c>{prefix}_{nameSlug}_{suffix}</c> — readable, so the HA
/// entity_id reads e.g. <c>binary_sensor.stashboard_jellyfin_running</c>.</item>
/// </list>
/// Discovery topic: <c>{discoveryPrefix}/{component}/{nodeId}/{suffix}/config</c>
/// (component is <c>binary_sensor</c> or <c>sensor</c>).
/// State topic: <c>{entityPrefix}/{component}/{objectId}/state</c> (retained).
/// V9.1 roll-up sensors live on the hub device itself; per-object sensors link to the
/// hub via <c>via_device</c>. Every entity references the single availability topic
/// <c>{entityPrefix}/status</c> registered as the broker Last Will.
/// </remarks>
public static class MqttDiscoveryBuilder
{
    public const string BinarySensor = "binary_sensor";
    public const string Sensor = "sensor";
    public const string OnlinePayload = "online";
    public const string OfflinePayload = "offline";
    public const string StateOn = "ON";
    public const string StateOff = "OFF";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        // Null device_class etc. should simply be omitted; we build the dict without nulls.
    };

    /// <summary>The single retained availability topic (the broker Last Will).</summary>
    public static string AvailabilityTopic(string entityPrefix) => $"{entityPrefix}/status";

    /// <summary>The hub device id (the <c>via_device</c> root, carries V9.1 roll-ups).</summary>
    public static string HubId(string entityPrefix) => $"{entityPrefix}_hub";

    /// <summary>
    /// Builds the full desired entity set for the current snapshot: the Stashboard
    /// hub status sensor first, then one entity per source signal (grouped into
    /// per-object devices linked to the hub by <c>via_device</c>; roll-ups attach to
    /// the hub device itself).
    /// </summary>
    public static IReadOnlyList<MqttDesiredEntity> BuildDesired(
        IReadOnlyList<MqttSourceEntity> sources, string discoveryPrefix, string entityPrefix,
        string hubDeviceName = "Stashboard", string manufacturer = "Stashboard")
    {
        var availability = AvailabilityTopic(entityPrefix);
        var hubId = HubId(entityPrefix);
        var result = new List<MqttDesiredEntity>(sources.Count + 1)
        {
            // Hub "online" connectivity sensor — gives the via_device root a real
            // entity so HA renders the hub device, and doubles as "is Stashboard up".
            BuildEntity(
                discoveryPrefix, entityPrefix, availability, manufacturer,
                component: BinarySensor,
                nodeId: hubId,
                deviceIdentifier: hubId,
                deviceName: hubDeviceName,
                deviceModel: "Integration",
                viaDevice: null,
                suffix: "status",
                deviceClass: "connectivity",
                source: null),
        };

        foreach (var s in sources)
        {
            var spec = Spec(s);
            if (s.Kind == MqttEntityKind.Rollup)
            {
                // A summary count that belongs to the hub device itself — no separate
                // device, no via_device (it IS the via_device root).
                result.Add(BuildEntity(
                    discoveryPrefix, entityPrefix, availability, manufacturer,
                    component: spec.Component,
                    nodeId: hubId,
                    deviceIdentifier: hubId,
                    deviceName: hubDeviceName,
                    deviceModel: "Integration",
                    viaDevice: null,
                    suffix: spec.Suffix,
                    deviceClass: spec.DeviceClass,
                    source: s,
                    stateClass: spec.StateClass,
                    unit: spec.Unit,
                    nameSlug: spec.Suffix,
                    entityPrefixForName: entityPrefix,
                    nameIsSuffixOnly: true));
                continue;
            }

            var nameSlug = Slug(s.DeviceName);
            // The HA device is keyed by the real object's DeviceKey (via the hashed
            // nodeId), NOT by name — so a Docker container `jellyfin` and a Proxmox LXC
            // `jellyfin` are two distinct devices and never merge. Sensors that DO belong
            // together share a device only because the provider gives them the same DeviceKey.
            var nodeId = $"{entityPrefix}_{nameSlug}_{ShortHash(s.DeviceKey)}";
            result.Add(BuildEntity(
                discoveryPrefix, entityPrefix, availability, manufacturer,
                component: spec.Component,
                nodeId: nodeId,
                deviceIdentifier: nodeId,
                deviceName: s.DeviceName,
                deviceModel: s.DeviceModel,
                viaDevice: hubId,
                suffix: spec.Suffix,
                deviceClass: spec.DeviceClass,
                source: s,
                stateClass: spec.StateClass,
                unit: spec.Unit,
                nameSlug: nameSlug,
                entityPrefixForName: entityPrefix));
        }

        return result;
    }

    private static MqttDesiredEntity BuildEntity(
        string discoveryPrefix, string entityPrefix, string availabilityTopic, string manufacturer,
        string component, string nodeId, string deviceIdentifier, string deviceName, string deviceModel,
        string? viaDevice, string suffix, string deviceClass, MqttSourceEntity? source,
        string? stateClass = null, string? unit = null,
        string? nameSlug = null, string? entityPrefixForName = null, bool nameIsSuffixOnly = false)
    {
        var objectId = $"{nodeId}_{suffix}";
        var discoveryTopic = $"{discoveryPrefix}/{component}/{nodeId}/{suffix}/config";
        var stateTopic = $"{entityPrefix}/{component}/{objectId}/state";
        // Readable friendly name: {prefix}_{nameSlug}_{suffix} for per-object entities,
        // {prefix}_{suffix} for hub roll-ups; falls back to the object id for the hub status.
        var name = nameSlug is not null && entityPrefixForName is not null
            ? (nameIsSuffixOnly ? $"{entityPrefixForName}_{nameSlug}" : $"{entityPrefixForName}_{nameSlug}_{suffix}")
            : objectId;

        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { deviceIdentifier },
            ["name"] = deviceName,
            ["model"] = deviceModel,
            ["manufacturer"] = manufacturer,
        };
        if (viaDevice is not null) device["via_device"] = viaDevice;

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["unique_id"] = objectId,
            ["object_id"] = objectId,
            ["state_topic"] = stateTopic,
            ["device_class"] = deviceClass,
            ["availability_topic"] = availabilityTopic,
            ["payload_available"] = OnlinePayload,
            ["payload_not_available"] = OfflinePayload,
            ["device"] = device,
        };
        // device_class is optional for plain numeric sensors (update counts / roll-ups).
        if (deviceClass.Length == 0) payload.Remove("device_class");

        if (component == BinarySensor)
        {
            payload["payload_on"] = StateOn;
            payload["payload_off"] = StateOff;
        }
        if (stateClass is not null) payload["state_class"] = stateClass;
        if (unit is not null) payload["unit_of_measurement"] = unit;

        // Attributes (node-alert breakdown) ride on their own retained topic.
        string? attributesTopic = null;
        string? attributesPayload = null;
        if (source?.Attributes is { Count: > 0 })
        {
            attributesTopic = $"{entityPrefix}/{component}/{objectId}/attributes";
            attributesPayload = JsonSerializer.Serialize(source.Attributes, JsonOpts);
            payload["json_attributes_topic"] = attributesTopic;
        }

        return new MqttDesiredEntity(
            objectId,
            discoveryTopic,
            JsonSerializer.Serialize(payload, JsonOpts),
            stateTopic,
            RenderState(source),
            attributesTopic,
            attributesPayload);
    }

    /// <summary>Renders the retained state payload for a source by its kind: ON/OFF
    /// for binary sensors, an integer for counts, ISO-8601 for the backup timestamp.
    /// <c>null</c> (the hub status, or an unknown reading) means "publish no state".</summary>
    private static string? RenderState(MqttSourceEntity? source)
    {
        if (source is null) return StateOn; // hub status sensor is always "online".
        return source.Kind switch
        {
            MqttEntityKind.UpdateCount or MqttEntityKind.Rollup =>
                source.Number is { } n ? ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture) : null,
            MqttEntityKind.BackupAge =>
                source.Timestamp is { } t ? t.ToString("o", CultureInfo.InvariantCulture) : null,
            _ => source.IsOn switch { true => StateOn, false => StateOff, null => null },
        };
    }

    /// <summary>Per-kind HA entity shape: component, id suffix, device_class, and
    /// (sensors only) state_class + unit.</summary>
    private sealed record KindSpec(string Component, string Suffix, string DeviceClass, string? StateClass, string? Unit);

    private static KindSpec Spec(MqttSourceEntity s) => s.Kind switch
    {
        MqttEntityKind.Running => new(BinarySensor, "running", "running", null, null),
        MqttEntityKind.Update => new(BinarySensor, "update", "update", null, null),
        MqttEntityKind.Health => new(BinarySensor, "health", "connectivity", null, null),
        MqttEntityKind.NodeAlert => new(BinarySensor, "alert", "problem", null, null),
        MqttEntityKind.UpdateCount => new(Sensor, "updates", "", "measurement", "updates"),
        MqttEntityKind.BackupAge => new(Sensor, "backup_age", "timestamp", null, null),
        MqttEntityKind.Rollup => new(Sensor, s.MetricKey ?? "metric", "", "measurement", null),
        _ => new(BinarySensor, "state", "running", null, null),
    };

    /// <summary>Lowercase, non-alphanumerics → underscore, collapsed and trimmed.</summary>
    public static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastUnderscore = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }
        var slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "x" : slug;
    }

    /// <summary>Stable 6-hex short hash of the device key — disambiguates same-named
    /// objects on different hosts without leaking a guid into the id.</summary>
    public static string ShortHash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes, 0, 3).ToLowerInvariant();
    }
}
