using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — pure builder that turns the source signals into Home Assistant
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
/// (a container's running + update, and a linked service's health).</item>
/// <item><b>object_id / unique_id</b> = <c>{nodeId}_{suffix}</c> (suffix:
/// running / update / health).</item>
/// <item><b>name</b> = <c>{prefix}_{nameSlug}_{suffix}</c> — readable, so the HA
/// entity_id reads e.g. <c>binary_sensor.stashboard_jellyfin_running</c>.</item>
/// </list>
/// Discovery topic: <c>{discoveryPrefix}/binary_sensor/{nodeId}/{suffix}/config</c>.
/// State topic: <c>{entityPrefix}/binary_sensor/{objectId}/state</c> (retained).
/// Every entity references the single availability topic
/// <c>{entityPrefix}/status</c> registered as the broker Last Will.
/// </remarks>
public static class MqttDiscoveryBuilder
{
    public const string Component = "binary_sensor";
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
    /// per-object devices linked to the hub by <c>via_device</c>).
    /// </summary>
    public static IReadOnlyList<MqttDesiredEntity> BuildDesired(
        IReadOnlyList<MqttSourceEntity> sources, string discoveryPrefix, string entityPrefix)
    {
        var availability = AvailabilityTopic(entityPrefix);
        var hubId = HubId(entityPrefix);
        var result = new List<MqttDesiredEntity>(sources.Count + 1)
        {
            // Hub "online" connectivity sensor — gives the via_device root a real
            // entity so HA renders the Stashboard hub, and doubles as "is Stashboard up".
            BuildEntity(
                discoveryPrefix, entityPrefix, availability,
                nodeId: hubId,
                deviceIdentifier: hubId,
                deviceName: "Stashboard",
                deviceModel: "Integration",
                viaDevice: null,
                suffix: "status",
                deviceClass: "connectivity",
                isOn: true),
        };

        foreach (var s in sources)
        {
            var nameSlug = Slug(s.DeviceName);
            // The HA device is keyed by the real object's DeviceKey (via the hashed
            // nodeId), NOT by name — so a Docker container `jellyfin` and a Proxmox LXC
            // `jellyfin` are two distinct devices and never merge. Sensors that DO belong
            // together (a container's running + update, and the health of a service the
            // user linked to that container) share a device only because the provider
            // gives them the same DeviceKey.
            var nodeId = $"{entityPrefix}_{nameSlug}_{ShortHash(s.DeviceKey)}";
            var (suffix, deviceClass) = Map(s.Kind);
            result.Add(BuildEntity(
                discoveryPrefix, entityPrefix, availability,
                nodeId: nodeId,
                deviceIdentifier: nodeId,
                deviceName: s.DeviceName,
                deviceModel: s.DeviceModel,
                viaDevice: hubId,
                suffix: suffix,
                deviceClass: deviceClass,
                isOn: s.IsOn,
                nameSlug: nameSlug,
                entityPrefixForName: entityPrefix));
        }

        return result;
    }

    private static MqttDesiredEntity BuildEntity(
        string discoveryPrefix, string entityPrefix, string availabilityTopic,
        string nodeId, string deviceIdentifier, string deviceName, string deviceModel, string? viaDevice,
        string suffix, string deviceClass, bool? isOn,
        string? nameSlug = null, string? entityPrefixForName = null)
    {
        var objectId = $"{nodeId}_{suffix}";
        var discoveryTopic = $"{discoveryPrefix}/{Component}/{nodeId}/{suffix}/config";
        var stateTopic = $"{entityPrefix}/{Component}/{objectId}/state";
        // Readable friendly name: {prefix}_{nameSlug}_{suffix}; falls back to the
        // object id for the hub (which has no separate name slug).
        var name = nameSlug is not null && entityPrefixForName is not null
            ? $"{entityPrefixForName}_{nameSlug}_{suffix}"
            : objectId;

        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { deviceIdentifier },
            ["name"] = deviceName,
            ["model"] = deviceModel,
            ["manufacturer"] = "Stashboard",
        };
        if (viaDevice is not null) device["via_device"] = viaDevice;

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["unique_id"] = objectId,
            ["object_id"] = objectId,
            ["state_topic"] = stateTopic,
            ["device_class"] = deviceClass,
            ["payload_on"] = StateOn,
            ["payload_off"] = StateOff,
            ["availability_topic"] = availabilityTopic,
            ["payload_available"] = OnlinePayload,
            ["payload_not_available"] = OfflinePayload,
            ["device"] = device,
        };

        var statePayload = isOn switch
        {
            true => StateOn,
            false => StateOff,
            null => null,
        };

        return new MqttDesiredEntity(
            objectId,
            discoveryTopic,
            JsonSerializer.Serialize(payload, JsonOpts),
            stateTopic,
            statePayload);
    }

    private static (string Suffix, string DeviceClass) Map(MqttEntityKind kind) => kind switch
    {
        MqttEntityKind.Running => ("running", "running"),
        MqttEntityKind.Update => ("update", "update"),
        MqttEntityKind.Health => ("health", "connectivity"),
        _ => ("state", "running"),
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
