using System.Text.Json;
using Stashboard.Api.Services.Mqtt;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.0 — the pure discovery-payload builder: id prefixing, device grouping,
/// state / availability topic wiring. No broker.
/// </summary>
public class MqttDiscoveryBuilderTests
{
    private static JsonElement Payload(MqttDesiredEntity e) => JsonDocument.Parse(e.DiscoveryPayload).RootElement;

    [Fact]
    public void Discovery_HasExpectedIds_TopicsAndAvailability()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Running, "docker:c1:jellyfin", "jellyfin", "Docker container", IsOn: true),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");

        var running = desired.Single(d => d.ObjectId.EndsWith("_running"));
        var p = Payload(running);

        // Node id, object_id, unique_id all start with the entity prefix.
        Assert.StartsWith("homeassistant/binary_sensor/stashboard_jellyfin_", running.DiscoveryTopic);
        Assert.EndsWith("/running/config", running.DiscoveryTopic);
        Assert.StartsWith("stashboard_", p.GetProperty("object_id").GetString());
        Assert.StartsWith("stashboard_", p.GetProperty("unique_id").GetString());
        Assert.StartsWith("stashboard_", p.GetProperty("name").GetString());

        // State topic is retained and pointed at by the discovery config.
        Assert.Equal(running.StateTopic, p.GetProperty("state_topic").GetString());
        Assert.Equal("stashboard/status", p.GetProperty("availability_topic").GetString());
        Assert.Equal("running", p.GetProperty("device_class").GetString());

        // Device grouping: keyed by the real object (per-source nodeId), linked to the hub.
        var device = p.GetProperty("device");
        Assert.Equal("jellyfin", device.GetProperty("name").GetString());
        Assert.StartsWith("stashboard_jellyfin_", device.GetProperty("identifiers")[0].GetString());
        Assert.Equal("stashboard_hub", device.GetProperty("via_device").GetString());

        // State payload reflects "running".
        Assert.Equal("ON", running.StatePayload);
    }

    [Fact]
    public void RunningAndUpdate_ShareOneDevice()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Running, "docker:c1:sonarr", "sonarr", "Docker container", IsOn: true),
            new(MqttEntityKind.Update, "docker:c1:sonarr", "sonarr", "Docker container", IsOn: false),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");

        var running = desired.Single(d => d.ObjectId.EndsWith("_running"));
        var update = desired.Single(d => d.ObjectId.EndsWith("_update"));

        string DeviceId(MqttDesiredEntity e) =>
            Payload(e).GetProperty("device").GetProperty("identifiers")[0].GetString()!;

        // Same device key ⇒ same HA device, two entities under it.
        Assert.Equal(DeviceId(running), DeviceId(update));
        Assert.Equal("update", Payload(update).GetProperty("device_class").GetString());
    }

    [Fact]
    public void SameDeviceKey_GroupsIntoOneDevice()
    {
        // A linked service's health (given the container's DeviceKey by the provider) and
        // that container's running sensor share one Home Assistant device.
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Health, "docker:c1:jellyfin", "jellyfin", "Docker container", IsOn: true),
            new(MqttEntityKind.Running, "docker:c1:jellyfin", "jellyfin", "Docker container", IsOn: true),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var health = desired.Single(d => d.ObjectId.EndsWith("_health"));
        var running = desired.Single(d => d.ObjectId.EndsWith("_running"));

        Assert.Equal(DeviceId(running), DeviceId(health));
        // …but each keeps its own unique discovery topic + object_id (no overwrite).
        Assert.NotEqual(health.DiscoveryTopic, running.DiscoveryTopic);
        Assert.NotEqual(health.ObjectId, running.ObjectId);
    }

    [Fact]
    public void SameNameDifferentObject_StaySeparateDevices()
    {
        // A Docker container and a Proxmox LXC that merely share the name "jellyfin" are
        // different machines: different DeviceKeys ⇒ different Home Assistant devices.
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Running, "docker:c1:jellyfin", "jellyfin", "Docker container", IsOn: true),
            new(MqttEntityKind.Running, "guest:p1:101", "jellyfin", "Proxmox LXC", IsOn: true),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var devices = desired
            .Where(d => d.ObjectId.EndsWith("_running"))
            .Select(DeviceId)
            .ToList();

        Assert.Equal(2, devices.Distinct().Count());
    }

    private static string DeviceId(MqttDesiredEntity e) =>
        Payload(e).GetProperty("device").GetProperty("identifiers")[0].GetString()!;

    [Fact]
    public void Hub_IsAlwaysPublished_AsConnectivity()
    {
        var desired = MqttDiscoveryBuilder.BuildDesired(new List<MqttSourceEntity>(), "homeassistant", "stashboard");

        var hub = Assert.Single(desired);
        Assert.Equal("stashboard_hub_status", hub.ObjectId);
        Assert.Equal("connectivity", Payload(hub).GetProperty("device_class").GetString());
    }

    [Fact]
    public void EntityPrefix_IsAppliedToEveryId()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Health, "service:1", "Sonarr", "Service", IsOn: true),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "homelab");

        foreach (var d in desired)
        {
            var p = Payload(d);
            Assert.StartsWith("homelab_", d.ObjectId);
            Assert.StartsWith("homelab_", p.GetProperty("object_id").GetString());
            Assert.StartsWith("homelab_", p.GetProperty("unique_id").GetString());
            Assert.StartsWith("homelab_", p.GetProperty("name").GetString());
            Assert.Contains("homelab/binary_sensor/", d.StateTopic);
        }
    }

    [Fact]
    public void UnknownState_PublishesNoStatePayload()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Health, "service:1", "Sonarr", "Service", IsOn: null),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var health = desired.Single(d => d.ObjectId.EndsWith("_health"));
        Assert.Null(health.StatePayload);
    }

    // ── V9.1 derived-signal sensors ────────────────────────────────────────────

    [Fact]
    public void UpdateCount_IsANumericSensor_OnTheContainersDevice()
    {
        // Running + update-count share the same DeviceKey ⇒ one device, the count over
        // the `sensor` component with a numeric state and no payload_on/off.
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Running, "docker:c1:sonarr", "sonarr", "Docker container", IsOn: true),
            new(MqttEntityKind.UpdateCount, "docker:c1:sonarr", "sonarr", "Docker container", Number: 4),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var count = desired.Single(d => d.ObjectId.EndsWith("_updates"));
        var p = Payload(count);

        Assert.StartsWith("homeassistant/sensor/", count.DiscoveryTopic);
        Assert.Equal("4", count.StatePayload);
        Assert.Equal("measurement", p.GetProperty("state_class").GetString());
        Assert.False(p.TryGetProperty("payload_on", out _));
        // Grouped onto the same device as the running sensor.
        var running = desired.Single(d => d.ObjectId.EndsWith("_running"));
        Assert.Equal(DeviceId(running), DeviceId(count));
    }

    [Fact]
    public void NodeAlert_IsAProblemSensor_WithCategoryAttributes()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["worst_severity"] = "crit",
            ["cpu"] = "crit",
            ["memory"] = "warn",
            ["storage"] = "ok",
        };
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.NodeAlert, "node:p1", "pve", "Proxmox node", IsOn: true, Attributes: attributes),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var alert = desired.Single(d => d.ObjectId.EndsWith("_alert"));
        var p = Payload(alert);

        Assert.StartsWith("homeassistant/binary_sensor/", alert.DiscoveryTopic);
        Assert.Equal("problem", p.GetProperty("device_class").GetString());
        Assert.Equal("ON", alert.StatePayload);

        // Attributes ride their own retained topic, referenced by the discovery config.
        Assert.NotNull(alert.AttributesTopic);
        Assert.Equal(alert.AttributesTopic, p.GetProperty("json_attributes_topic").GetString());
        var attrs = JsonDocument.Parse(alert.AttributesPayload!).RootElement;
        Assert.Equal("crit", attrs.GetProperty("worst_severity").GetString());
        Assert.Equal("warn", attrs.GetProperty("memory").GetString());
    }

    [Fact]
    public void BackupAge_IsATimestampSensor_InIso8601()
    {
        var ts = new DateTimeOffset(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.BackupAge, "guest:p1:101", "ct1", "Proxmox LXC", Timestamp: ts),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var backup = desired.Single(d => d.ObjectId.EndsWith("_backup_age"));
        var p = Payload(backup);

        Assert.StartsWith("homeassistant/sensor/", backup.DiscoveryTopic);
        Assert.Equal("timestamp", p.GetProperty("device_class").GetString());
        Assert.Equal(ts, DateTimeOffset.Parse(backup.StatePayload!));
    }

    [Fact]
    public void Rollups_LiveOnTheHubDevice()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Rollup, "hub", "Stashboard", "Integration", Number: 7, MetricKey: "containers_running"),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");
        var rollup = desired.Single(d => d.ObjectId.EndsWith("_containers_running"));
        var p = Payload(rollup);

        Assert.StartsWith("homeassistant/sensor/", rollup.DiscoveryTopic);
        Assert.Equal("7", rollup.StatePayload);
        // It belongs to the hub device itself (same identifier as the hub status sensor),
        // so it has no via_device of its own.
        Assert.Equal("stashboard_hub", DeviceId(rollup));
        Assert.False(p.GetProperty("device").TryGetProperty("via_device", out _));
        Assert.Equal("stashboard_hub_containers_running", rollup.ObjectId);
    }

    [Fact]
    public void CustomHubName_AndManufacturer_FlowToEveryDevice()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.Running, "docker:c1:jellyfin", "jellyfin", "Docker container", IsOn: true),
            new(MqttEntityKind.Rollup, "hub", "Stashboard", "Integration", Number: 3, MetricKey: "containers_running"),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(
            sources, "homeassistant", "stashboard", hubDeviceName: "Homelab", manufacturer: "Acme");

        // Manufacturer is stamped on every device (per-object + hub).
        Assert.All(desired, d =>
            Assert.Equal("Acme", Payload(d).GetProperty("device").GetProperty("manufacturer").GetString()));

        // The hub device (status sensor + roll-up) carries the custom name.
        var hub = desired.Single(d => d.ObjectId == "stashboard_hub_status");
        Assert.Equal("Homelab", Payload(hub).GetProperty("device").GetProperty("name").GetString());
        var rollup = desired.Single(d => d.ObjectId.EndsWith("_containers_running"));
        Assert.Equal("Homelab", Payload(rollup).GetProperty("device").GetProperty("name").GetString());

        // Per-object devices keep their own name (not the hub name).
        var running = desired.Single(d => d.ObjectId.Contains("_jellyfin_"));
        Assert.Equal("jellyfin", Payload(running).GetProperty("device").GetProperty("name").GetString());
    }

    [Fact]
    public void EveryEntity_ReferencesTheSharedAvailabilityTopic()
    {
        var sources = new List<MqttSourceEntity>
        {
            new(MqttEntityKind.UpdateCount, "node:p1", "pve", "Proxmox node", Number: 2),
            new(MqttEntityKind.NodeAlert, "node:p1", "pve", "Proxmox node", IsOn: false,
                Attributes: new Dictionary<string, object?> { ["worst_severity"] = "ok" }),
            new(MqttEntityKind.BackupAge, "guest:p1:101", "ct1", "Proxmox LXC", Timestamp: DateTimeOffset.UnixEpoch),
            new(MqttEntityKind.Rollup, "hub", "Stashboard", "Integration", Number: 1, MetricKey: "updates_pending"),
        };

        var desired = MqttDiscoveryBuilder.BuildDesired(sources, "homeassistant", "stashboard");

        Assert.All(desired, d =>
            Assert.Equal("stashboard/status", Payload(d).GetProperty("availability_topic").GetString()));
    }
}

