using Microsoft.Extensions.Options;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — the background MQTT publisher. Holds one long-lived broker connection and,
/// every cycle, reconciles the broker's retained Home Assistant Discovery + state
/// topics against the current estate (via <see cref="IMqttEntityStateProvider"/> and
/// <see cref="MqttPublishReconciler"/>). Connects with a Last Will so all entities go
/// <c>unavailable</c> when Stashboard stops or the link drops; reconnects on the next
/// cycle after a broker drop; and applies settings changes (enable/disable, broker
/// params, prefixes) without a restart. Disabled or unconfigured → it simply idles.
/// </summary>
public sealed class MqttPublisherService(
    IServiceScopeFactory scopeFactory,
    IMqttBrokerClient broker,
    MqttPublishReconciler reconciler,
    IOptions<MqttOptions> options,
    ILogger<MqttPublisherService> logger) : BackgroundService
{
    // A full discovery+state refresh roughly every 5 minutes regardless of changes,
    // so a long-lived HA still re-syncs even if it missed a retained message.
    private const int FullRefreshEveryCycles = 10;

    private string? _connectedSignature;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations + the rest of the app settle before the first connect.
        try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); }
        catch (TaskCanceledException) { return; }

        var cycle = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = Math.Max(5, options.Value.RefreshIntervalSeconds);
            try
            {
                await RunCycleAsync(cycle, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MQTT publisher cycle failed");
            }

            cycle++;
            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }

        // Graceful stop — flip availability to offline so HA marks entities unavailable.
        try { await broker.DisconnectAsync(CancellationToken.None); } catch { /* shutting down */ }
    }

    /// <summary>One publish cycle: (re)connect if needed, then reconcile retained
    /// topics against the current estate. Internal so it can be driven directly in
    /// tests (reconnect, disable, LWT) without the timing loop.</summary>
    internal async Task RunCycleAsync(int cycle, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IMqttSettingsService>();
        var settings = await settingsService.GetResolvedAsync(cancellationToken);

        // Disabled / unconfigured → ensure we're disconnected and idle.
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
        {
            if (broker.IsConnected)
            {
                await broker.DisconnectAsync(cancellationToken);
                reconciler.Reset();
                _connectedSignature = null;
                logger.LogInformation("MQTT integration disabled — disconnected from broker");
            }
            return;
        }

        var availabilityTopic = MqttDiscoveryBuilder.AvailabilityTopic(settings.EntityPrefix);
        var connection = new MqttConnectionSettings(
            settings.Host, settings.Port, settings.UseTls, settings.AllowUntrustedTls,
            string.IsNullOrEmpty(settings.Username) ? null : settings.Username,
            string.IsNullOrEmpty(settings.Password) ? null : settings.Password,
            settings.ClientId, availabilityTopic,
            MqttDiscoveryBuilder.OnlinePayload, MqttDiscoveryBuilder.OfflinePayload);

        // Broker params changed → reconnect with the new options.
        var signature = ConnectionSignature(connection);
        if (broker.IsConnected && signature != _connectedSignature)
        {
            await broker.DisconnectAsync(cancellationToken);
            reconciler.Reset();
            _connectedSignature = null;
        }

        var freshlyConnected = false;
        if (!broker.IsConnected)
        {
            await broker.ConnectAsync(connection, cancellationToken);
            await broker.PublishAsync(availabilityTopic, MqttDiscoveryBuilder.OnlinePayload, retain: true, cancellationToken);
            reconciler.Reset();
            _connectedSignature = signature;
            freshlyConnected = true;
        }

        var fullRefresh = freshlyConnected || cycle % FullRefreshEveryCycles == 0;
        if (fullRefresh)
            await broker.PublishAsync(availabilityTopic, MqttDiscoveryBuilder.OnlinePayload, retain: true, cancellationToken);

        var provider = scope.ServiceProvider.GetRequiredService<IMqttEntityStateProvider>();
        var sources = await provider.GetEntitiesAsync(cancellationToken);

        await reconciler.ReconcileAsync(
            broker, sources, settings.DiscoveryPrefix, settings.EntityPrefix, fullRefresh, cancellationToken,
            settings.DeviceName, settings.Manufacturer);
    }

    private static string ConnectionSignature(MqttConnectionSettings s) =>
        string.Join('|', s.Host, s.Port, s.UseTls, s.AllowUntrustedTls, s.Username ?? "", s.Password ?? "", s.ClientId);
}
