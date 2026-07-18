using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// In-memory fake broker for the publisher / reconciler tests. Records every
/// publish (topic → last payload + retain) and every connect, and lets a test
/// simulate a broker drop. No network.
/// </summary>
public sealed class FakeMqttBrokerClient : IMqttBrokerClient
{
    public List<(string Topic, string Payload, bool Retain)> Published { get; } = new();
    public List<MqttConnectionSettings> Connects { get; } = new();
    public int ConnectCount => Connects.Count;
    public bool IsConnected { get; private set; }
    public bool FailNextConnect { get; set; }

    /// <summary>The broker's retained view: topic → last non-empty payload (empty clears).</summary>
    public Dictionary<string, string> Retained { get; } = new();

    public Task ConnectAsync(MqttConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        if (FailNextConnect)
        {
            FailNextConnect = false;
            throw new InvalidOperationException("broker unreachable");
        }
        Connects.Add(settings);
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, payload, retain));
        if (retain)
        {
            if (string.IsNullOrEmpty(payload)) Retained.Remove(topic);
            else Retained[topic] = payload;
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    /// <summary>Simulate a connection drop (the next cycle should reconnect).</summary>
    public void Drop() => IsConnected = false;

    /// <summary>Clears the recorded publish log (keeps the retained view + connection state).</summary>
    public void ClearLog() => Published.Clear();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
