using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V10.0 — app-wide Apprise notification configuration, stored as a single row so
/// it can be edited at runtime from the Notifications settings page instead of
/// living only in <c>appsettings</c> / env vars. Seeded from the bound
/// <see cref="Notifications.AppriseOptions"/> on first access so an existing
/// deployment keeps its configured values until an operator edits them.
/// </summary>
/// <remarks>
/// Mirrors <see cref="EmailSettingsEntity"/> / <see cref="MqttSettingsEntity"/>:
/// the Apprise URLs carry secrets (webhook tokens, API keys) so the whole list is
/// encrypted at rest (<see cref="UrlsEncrypted"/>) and never returned to the API
/// (presence flag + non-secret schemes only). The channel is off by default
/// (<see cref="Enabled"/>) — Stashboard sends nothing until an operator turns it on
/// and points it at their own Apprise instance / the stateless endpoint.
/// </remarks>
public class AppriseSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one apprise-settings row.</summary>
    public static readonly Guid SingletonId = new("d0000000-0000-0000-0000-000000000010");

    /// <summary>Master switch for the whole Apprise channel. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base URL of the Apprise API the notifications POST to — either a self-hosted
    /// Apprise API root (e.g. <c>http://apprise:8000</c>) or the full stateless
    /// <c>/notify</c> endpoint. The sender appends <c>/notify</c> when it isn't
    /// already present.
    /// </summary>
    [MaxLength(512)]
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// AES-256-GCM ciphertext of the configured Apprise URLs (one per line:
    /// <c>discord://…</c>, <c>ntfy://…</c>, <c>gotify://…</c>, …). Plaintext is never
    /// persisted or returned — the API exposes only a presence flag and the
    /// non-secret schemes.
    /// </summary>
    public string? UrlsEncrypted { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
