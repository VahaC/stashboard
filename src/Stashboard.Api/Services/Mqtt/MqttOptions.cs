namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — MQTT / Home Assistant integration configuration bound from the "Mqtt"
/// section in appsettings. Used only to seed the runtime-editable
/// <see cref="Data.MqttSettingsEntity"/> row on first access; live settings are
/// read from the DB via <see cref="IMqttSettingsService"/>.
/// </summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; }
    public bool AllowUntrustedTls { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ClientId { get; set; } = "stashboard";
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public string EntityPrefix { get; set; } = "stashboard";
    public string DeviceName { get; set; } = "Stashboard";
    public string Manufacturer { get; set; } = "Stashboard";

    /// <summary>Publisher loop cadence (full state refresh interval), in seconds.</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;
}
