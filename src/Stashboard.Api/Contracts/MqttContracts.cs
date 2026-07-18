using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

/// <summary>
/// V9.0 — masked view of the app-wide MQTT / Home Assistant integration settings.
/// The broker password is never returned — only <see cref="HasPassword"/>.
/// </summary>
public sealed record MqttSettingsResponse(
    bool Enabled,
    string Host,
    int Port,
    bool UseTls,
    bool AllowUntrustedTls,
    string Username,
    bool HasPassword,
    string ClientId,
    string DiscoveryPrefix,
    string EntityPrefix);

/// <summary>V9.0 — update payload for the MQTT integration settings.</summary>
public sealed record UpdateMqttSettingsRequest(
    bool Enabled,
    [MaxLength(256)] string? Host,
    [Range(1, 65535)] int Port,
    bool UseTls,
    bool AllowUntrustedTls,
    [MaxLength(256)] string? Username,
    // Tri-state secret: omit / Keep to preserve the stored password, Set to replace, Clear to drop.
    SecretValueUpsert? Password,
    [MaxLength(128)] string? ClientId,
    [MaxLength(64)] string? DiscoveryPrefix,
    [MaxLength(64)] string? EntityPrefix);

/// <summary>V9.0 — outcome of the "Test connection" button: did Stashboard reach the broker?</summary>
public sealed record MqttTestResponse(bool Reachable, string? Error);
