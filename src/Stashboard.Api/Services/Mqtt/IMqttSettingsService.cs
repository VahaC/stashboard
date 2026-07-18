using Stashboard.Api.Contracts;

namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// Fully-resolved, decrypted MQTT settings used by the publisher at connect-time.
/// <see cref="Password"/> is plaintext — never log it. <see cref="EntityPrefix"/>
/// and <see cref="DiscoveryPrefix"/> are normalised (trimmed, non-empty defaults).
/// </summary>
public sealed record ResolvedMqttSettings(
    bool Enabled,
    string Host,
    int Port,
    bool UseTls,
    bool AllowUntrustedTls,
    string Username,
    string Password,
    string ClientId,
    string DiscoveryPrefix,
    string EntityPrefix);

/// <summary>
/// Reads and writes the single app-wide MQTT-settings row. The row is created on
/// first access from the bound <see cref="MqttOptions"/> so an existing deployment
/// keeps its configured values until an operator edits them in the UI. Mirrors
/// <see cref="Notifications.IEmailSettingsService"/>.
/// </summary>
public interface IMqttSettingsService
{
    /// <summary>Loads (creating if needed) the settings and decrypts the password for the publisher.</summary>
    Task<ResolvedMqttSettings> GetResolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>Masked view for the API — password is never returned, only a presence flag.</summary>
    Task<MqttSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists edited settings, encrypting the password (tri-state: keep / set / clear).</summary>
    Task UpdateAsync(UpdateMqttSettingsRequest request, CancellationToken cancellationToken = default);
}
