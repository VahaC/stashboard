using Stashboard.Api.Services.Mqtt;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.0 — the diffing core: retained discovery + state on first publish, no spam on
/// an unchanged tick, a fresh state on a transition, retained-topic clearing on
/// removal, and re-publish-under-new-ids + clear-old-ids on an entity-prefix change.
/// </summary>
public class MqttPublishReconcilerTests
{
    private static List<MqttSourceEntity> Container(string name, bool running) => new()
    {
        new(MqttEntityKind.Running, $"docker:c1:{name}", name, "Docker container", running),
    };

    private static async Task<(FakeMqttBrokerClient, MqttPublishReconciler)> SeedAsync(List<MqttSourceEntity> sources)
    {
        var client = new FakeMqttBrokerClient();
        var reconciler = new MqttPublishReconciler();
        await reconciler.ReconcileAsync(client, sources, "homeassistant", "stashboard", forceFullRefresh: false);
        return (client, reconciler);
    }

    [Fact]
    public async Task FirstReconcile_PublishesRetainedDiscoveryAndState()
    {
        var (client, _) = await SeedAsync(Container("jellyfin", running: true));

        // Discovery + state for the container are retained.
        var discovery = client.Published.Single(p => p.Topic.Contains("/running/config"));
        Assert.True(discovery.Retain);

        var state = client.Published.Single(p => p.Topic.EndsWith("_running/state"));
        Assert.Equal("ON", state.Payload);
        Assert.True(state.Retain);
    }

    [Fact]
    public async Task UnchangedTick_PublishesNothing()
    {
        var sources = Container("jellyfin", running: true);
        var (client, reconciler) = await SeedAsync(sources);
        client.ClearLog();

        await reconciler.ReconcileAsync(client, sources, "homeassistant", "stashboard", forceFullRefresh: false);

        Assert.Empty(client.Published);
    }

    [Fact]
    public async Task Transition_RepublishesStateOnly()
    {
        var (client, reconciler) = await SeedAsync(Container("jellyfin", running: true));
        client.ClearLog();

        await reconciler.ReconcileAsync(client, Container("jellyfin", running: false), "homeassistant", "stashboard", forceFullRefresh: false);

        var state = client.Published.Single(p => p.Topic.EndsWith("_running/state"));
        Assert.Equal("OFF", state.Payload);
        // No discovery re-publish on a pure state change.
        Assert.DoesNotContain(client.Published, p => p.Topic.Contains("/config"));
    }

    [Fact]
    public async Task RemovedEntity_ClearsRetainedDiscoveryAndState()
    {
        var (client, reconciler) = await SeedAsync(Container("jellyfin", running: true));
        client.ClearLog();

        // Container disappeared — only the hub remains desired.
        await reconciler.ReconcileAsync(client, new List<MqttSourceEntity>(), "homeassistant", "stashboard", forceFullRefresh: false);

        var cleared = client.Published.Where(p => p.Payload == "" && p.Retain).ToList();
        Assert.Contains(cleared, p => p.Topic.Contains("/running/config"));
        Assert.Contains(cleared, p => p.Topic.EndsWith("_running/state"));
        // And the retained view no longer holds the container topics.
        Assert.DoesNotContain(client.Retained.Keys, k => k.Contains("_running"));
    }

    [Fact]
    public async Task PrefixChange_ClearsOldIdsAndPublishesNewIds()
    {
        var sources = Container("jellyfin", running: true);
        var (client, reconciler) = await SeedAsync(sources);
        client.ClearLog();

        await reconciler.ReconcileAsync(client, sources, "homeassistant", "homelab", forceFullRefresh: false);

        // Old stashboard_* discovery/state topics are cleared…
        Assert.Contains(client.Published, p => p.Topic.Contains("stashboard_jellyfin_") && p.Payload == "" && p.Retain);
        // …and new homelab_* topics are published.
        Assert.Contains(client.Published, p => p.Topic.Contains("homelab_jellyfin_") && p.Payload != "" && p.Topic.Contains("/config"));
        Assert.DoesNotContain(client.Retained.Keys, k => k.Contains("stashboard_jellyfin_"));
        Assert.Contains(client.Retained.Keys, k => k.Contains("homelab_jellyfin_"));
    }

    [Fact]
    public async Task ForceRefresh_RepublishesEvenWhenUnchanged()
    {
        var sources = Container("jellyfin", running: true);
        var (client, reconciler) = await SeedAsync(sources);
        client.ClearLog();

        await reconciler.ReconcileAsync(client, sources, "homeassistant", "stashboard", forceFullRefresh: true);

        Assert.Contains(client.Published, p => p.Topic.Contains("/running/config"));
        Assert.Contains(client.Published, p => p.Topic.EndsWith("_running/state"));
    }

    // ── V9.1 derived signals ────────────────────────────────────────────────────

    private static List<MqttSourceEntity> NodeAlert(bool on, string worst) => new()
    {
        new(MqttEntityKind.NodeAlert, "node:p1", "pve", "Proxmox node", IsOn: on,
            Attributes: new Dictionary<string, object?> { ["worst_severity"] = worst, ["cpu"] = worst }),
    };

    [Fact]
    public async Task UpdateCount_RefreshesOnAChangedTick()
    {
        var two = new List<MqttSourceEntity> { new(MqttEntityKind.UpdateCount, "dockerhost:c1", "den", "Docker host", Number: 2) };
        var (client, reconciler) = await SeedAsync(two);
        Assert.Contains(client.Published, p => p.Topic.EndsWith("_updates/state") && p.Payload == "2");
        client.ClearLog();

        var five = new List<MqttSourceEntity> { new(MqttEntityKind.UpdateCount, "dockerhost:c1", "den", "Docker host", Number: 5) };
        await reconciler.ReconcileAsync(client, five, "homeassistant", "stashboard", forceFullRefresh: false);

        var state = client.Published.Single(p => p.Topic.EndsWith("_updates/state"));
        Assert.Equal("5", state.Payload);
        Assert.DoesNotContain(client.Published, p => p.Topic.Contains("/config")); // count-only change
    }

    [Fact]
    public async Task NodeAlert_FlipsOn_PublishesStateAndAttributes()
    {
        var (client, reconciler) = await SeedAsync(NodeAlert(on: false, worst: "ok"));
        client.ClearLog();

        await reconciler.ReconcileAsync(client, NodeAlert(on: true, worst: "crit"), "homeassistant", "stashboard", forceFullRefresh: false);

        Assert.Equal("ON", client.Published.Single(p => p.Topic.EndsWith("_alert/state")).Payload);
        var attrs = client.Published.Single(p => p.Topic.EndsWith("_alert/attributes"));
        Assert.Contains("crit", attrs.Payload);
        Assert.True(attrs.Retain);
    }

    [Fact]
    public async Task NodeAlert_Disappears_ClearsStateAndAttributes()
    {
        var (client, reconciler) = await SeedAsync(NodeAlert(on: true, worst: "warn"));
        client.ClearLog();

        // Host removed — only the hub remains desired.
        await reconciler.ReconcileAsync(client, new List<MqttSourceEntity>(), "homeassistant", "stashboard", forceFullRefresh: false);

        var cleared = client.Published.Where(p => p.Payload == "" && p.Retain).ToList();
        Assert.Contains(cleared, p => p.Topic.EndsWith("/alert/config"));
        Assert.Contains(cleared, p => p.Topic.EndsWith("_alert/state"));
        Assert.Contains(cleared, p => p.Topic.EndsWith("_alert/attributes"));
        Assert.DoesNotContain(client.Retained.Keys, k => k.Contains("_alert"));
    }
}
