using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Services;

/// <summary>
/// V9.0 — concrete <see cref="IMqttBrokerClient"/> over MQTTnet. Holds one
/// long-lived client; <see cref="ConnectAsync"/> registers the Last Will so every
/// discovered Home Assistant entity flips to <c>unavailable</c> when the session
/// drops. All publishes are QoS 1 (at-least-once) so a retained discovery / state
/// message survives a transient hiccup.
/// </summary>
public sealed class MqttNetBrokerClient : IMqttBrokerClient
{
    private readonly ILogger<MqttNetBrokerClient> _logger;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private MqttConnectionSettings? _settings;
    private bool _expectingDisconnect;

    public MqttNetBrokerClient(ILogger<MqttNetBrokerClient> logger)
    {
        _logger = logger;
        // Surface *why* the broker dropped us — the common cause of a connect-then-
        // immediately-disconnect loop is an auth / ACL refusal (anonymous client not
        // allowed to publish) or a duplicate client id, both of which the reason
        // string makes obvious instead of a bare "client is not connected".
        _client.DisconnectedAsync += e =>
        {
            if (!_expectingDisconnect && e.ClientWasConnected)
                _logger.LogWarning(
                    e.Exception,
                    "MQTT disconnected by broker: reason={Reason} reasonString={ReasonString}. " +
                    "Common causes: wrong/empty broker credentials, a publish ACL that blocks the entity " +
                    "prefix, or another client using the same client id.",
                    e.Reason, e.ReasonString);
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(MqttConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        _expectingDisconnect = false;

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(settings.ClientId)
            .WithCleanSession(true)
            // Last Will — a single retained availability topic every entity references.
            .WithWillTopic(settings.AvailabilityTopic)
            .WithWillPayload(settings.OfflinePayload)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(settings.Username))
            builder = builder.WithCredentials(settings.Username, settings.Password ?? "");

        if (settings.UseTls)
        {
            var tls = new MqttClientTlsOptionsBuilder()
                .UseTls(true)
                .WithAllowUntrustedCertificates(settings.AllowUntrustedTls)
                .WithIgnoreCertificateChainErrors(settings.AllowUntrustedTls)
                .WithIgnoreCertificateRevocationErrors(settings.AllowUntrustedTls)
                .Build();
            builder = builder.WithTlsOptions(tls);
        }

        await _client.ConnectAsync(builder.Build(), cancellationToken);
        _logger.LogInformation("MQTT connected to {Host}:{Port} as {ClientId}", settings.Host, settings.Port, settings.ClientId);
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected) return;
        _expectingDisconnect = true;
        try
        {
            // Best-effort graceful "offline" before tearing the socket down (the LWT
            // covers an ungraceful drop). Retained so HA sees it on reconnect.
            if (_settings is { } s)
                await PublishAsync(s.AvailabilityTopic, s.OfflinePayload, retain: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT graceful-offline publish failed; disconnecting anyway");
        }
        await _client.DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); }
        catch { /* disposing — ignore */ }
        _client.Dispose();
    }
}
