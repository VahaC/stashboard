using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.Mqtt;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.0 — the publisher lifecycle: it connects with a Last Will, reconnects after a
/// broker drop, publishes the retained "online" availability, and disconnects when
/// the integration is turned off.
/// </summary>
public class MqttPublisherServiceTests
{
    private sealed class FakeSettings : IMqttSettingsService
    {
        public ResolvedMqttSettings Resolved { get; set; } =
            new(true, "mqtt.lan", 1883, false, false, "", "", "stashboard", "homeassistant", "stashboard");
        public Task<ResolvedMqttSettings> GetResolvedAsync(CancellationToken ct = default) => Task.FromResult(Resolved);
        public Task<MqttSettingsResponse> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(UpdateMqttSettingsRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeProvider : IMqttEntityStateProvider
    {
        public List<MqttSourceEntity> Entities { get; } = new()
        {
            new(MqttEntityKind.Running, "docker:c1:jellyfin", "jellyfin", "Docker container", true),
        };
        public Task<IReadOnlyList<MqttSourceEntity>> GetEntitiesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MqttSourceEntity>>(Entities);
    }

    private static (MqttPublisherService Service, FakeMqttBrokerClient Broker, FakeSettings Settings) Build()
    {
        var settings = new FakeSettings();
        var services = new ServiceCollection();
        services.AddScoped<IMqttSettingsService>(_ => settings);
        services.AddScoped<IMqttEntityStateProvider, FakeProvider>();
        var provider = services.BuildServiceProvider();

        var broker = new FakeMqttBrokerClient();
        var publisher = new MqttPublisherService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            broker,
            new MqttPublishReconciler(),
            Options.Create(new MqttOptions()),
            NullLogger<MqttPublisherService>.Instance);
        return (publisher, broker, settings);
    }

    [Fact]
    public async Task FirstCycle_ConnectsWithLastWill_AndPublishesOnline()
    {
        var (service, broker, _) = Build();

        await service.RunCycleAsync(0, CancellationToken.None);

        Assert.Equal(1, broker.ConnectCount);
        var lwt = broker.Connects[0];
        Assert.Equal("stashboard/status", lwt.AvailabilityTopic);
        Assert.Equal("offline", lwt.OfflinePayload);
        Assert.Contains(broker.Published, p => p.Topic == "stashboard/status" && p.Payload == "online" && p.Retain);
    }

    [Fact]
    public async Task BrokerDrop_Reconnects_OnNextCycle()
    {
        var (service, broker, _) = Build();

        await service.RunCycleAsync(0, CancellationToken.None);
        Assert.Equal(1, broker.ConnectCount);

        broker.Drop(); // simulate a broker / network drop
        await service.RunCycleAsync(1, CancellationToken.None);

        Assert.Equal(2, broker.ConnectCount);
        Assert.True(broker.IsConnected);
    }

    [Fact]
    public async Task Disabling_Disconnects()
    {
        var (service, broker, settings) = Build();

        await service.RunCycleAsync(0, CancellationToken.None);
        Assert.True(broker.IsConnected);

        settings.Resolved = settings.Resolved with { Enabled = false };
        await service.RunCycleAsync(1, CancellationToken.None);

        Assert.False(broker.IsConnected);
    }

    [Fact]
    public async Task NoHostConfigured_NeverConnects()
    {
        var (service, broker, settings) = Build();
        settings.Resolved = settings.Resolved with { Host = "" };

        await service.RunCycleAsync(0, CancellationToken.None);

        Assert.Equal(0, broker.ConnectCount);
    }
}
