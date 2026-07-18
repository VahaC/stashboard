using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — the stateful diffing core of the publisher. Holds what it last published
/// (per entity) so each cycle only writes what changed: new entities get retained
/// discovery + state, transitions get a fresh state, and entities that disappeared
/// (a removed container / guest / service, or every id after an entity-prefix change)
/// get their retained discovery + state topics cleared so Home Assistant removes
/// them rather than leaving an orphan. Pure aside from the injected broker client,
/// so it's unit-tested against a fake.
/// </summary>
public sealed class MqttPublishReconciler
{
    private sealed record Tracked(string DiscoveryTopic, string StateTopic, string DiscoveryPayload, string? StatePayload);

    private readonly Dictionary<string, Tracked> _published = new(StringComparer.Ordinal);

    /// <summary>Object ids currently believed to be live on the broker (test/diagnostic hook).</summary>
    public IReadOnlyCollection<string> TrackedObjectIds => _published.Keys;

    /// <summary>Forgets all tracking (e.g. after a reconnect) so the next cycle republishes in full.</summary>
    public void Reset() => _published.Clear();

    /// <summary>
    /// Reconciles the broker's retained topics against <paramref name="sources"/>.
    /// </summary>
    /// <param name="forceFullRefresh">When true, republish every entity's discovery +
    /// state even if unchanged (used right after a (re)connect and on the periodic
    /// full refresh) so a freshly-(re)connected HA gets the complete picture.</param>
    public async Task ReconcileAsync(
        IMqttBrokerClient client,
        IReadOnlyList<MqttSourceEntity> sources,
        string discoveryPrefix,
        string entityPrefix,
        bool forceFullRefresh,
        CancellationToken cancellationToken = default)
    {
        var desired = MqttDiscoveryBuilder.BuildDesired(sources, discoveryPrefix, entityPrefix);
        var desiredById = new Dictionary<string, MqttDesiredEntity>(StringComparer.Ordinal);
        foreach (var d in desired)
            desiredById[d.ObjectId] = d; // last-writer-wins de-dupe on a stable id

        // ── Removals: tracked but no longer desired → clear retained discovery + state.
        foreach (var stale in _published.Where(kv => !desiredById.ContainsKey(kv.Key)).ToList())
        {
            await client.PublishAsync(stale.Value.DiscoveryTopic, "", retain: true, cancellationToken);
            await client.PublishAsync(stale.Value.StateTopic, "", retain: true, cancellationToken);
            _published.Remove(stale.Key);
        }

        // ── Upserts.
        foreach (var d in desiredById.Values)
        {
            _published.TryGetValue(d.ObjectId, out var prev);

            var discoveryChanged = prev is null || prev.DiscoveryPayload != d.DiscoveryPayload;
            if (discoveryChanged || forceFullRefresh)
                await client.PublishAsync(d.DiscoveryTopic, d.DiscoveryPayload, retain: true, cancellationToken);

            // Only publish state when it's known; an unchanged tick (no full refresh)
            // is intentionally silent so we don't spam the broker every cycle.
            var stateChanged = prev is null || prev.StatePayload != d.StatePayload;
            if (d.StatePayload is not null && (stateChanged || forceFullRefresh))
                await client.PublishAsync(d.StateTopic, d.StatePayload, retain: true, cancellationToken);

            _published[d.ObjectId] = new Tracked(d.DiscoveryTopic, d.StateTopic, d.DiscoveryPayload, d.StatePayload);
        }
    }
}
