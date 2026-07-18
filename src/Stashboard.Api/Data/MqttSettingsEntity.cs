using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V9.0 — app-wide MQTT / Home Assistant integration configuration, stored as a
/// single row so it can be edited at runtime from the Settings page instead of
/// living only in <c>appsettings</c> / env vars. Seeded from the bound
/// <see cref="Services.Mqtt.MqttOptions"/> on first access so an existing
/// deployment keeps its configured values until an operator edits them.
/// </summary>
/// <remarks>
/// Mirrors <see cref="EmailSettingsEntity"/>: the broker password is encrypted
/// at rest (<see cref="PasswordEncrypted"/>) and never returned to the API
/// (presence flag only). The integration is off by default
/// (<see cref="Enabled"/>) — the publisher holds no broker connection until an
/// operator turns it on and points it at their existing broker.
/// </remarks>
public class MqttSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one mqtt-settings row.</summary>
    public static readonly Guid SingletonId = new("d0000000-0000-0000-0000-000000000009");

    /// <summary>Master switch for the whole integration. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Broker hostname / IP (e.g. the user's existing Mosquitto).</summary>
    [MaxLength(256)]
    public string Host { get; set; } = "";

    public int Port { get; set; } = 1883;

    /// <summary>Connect over TLS (typically port 8883).</summary>
    public bool UseTls { get; set; }

    /// <summary>Accept a self-signed / untrusted broker certificate when <see cref="UseTls"/> is on.</summary>
    public bool AllowUntrustedTls { get; set; }

    [MaxLength(256)]
    public string Username { get; set; } = "";

    /// <summary>AES-256-GCM ciphertext of the broker password. Plaintext is never persisted or returned.</summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>MQTT client id Stashboard connects with.</summary>
    [MaxLength(128)]
    public string ClientId { get; set; } = "stashboard";

    /// <summary>HA MQTT-Discovery topic prefix (HA default <c>homeassistant</c>).</summary>
    [MaxLength(64)]
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>
    /// Entity prefix applied to every published sensor's node id / object_id /
    /// unique_id (default <c>stashboard</c>) so the entities are trivial to spot,
    /// group and filter in Home Assistant and never collide with other producers.
    /// </summary>
    [MaxLength(64)]
    public string EntityPrefix { get; set; } = "stashboard";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
