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
}
