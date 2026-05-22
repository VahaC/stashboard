using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// App-wide SMTP / email-sender configuration, stored as a single row so it can be
/// edited at runtime from the UI instead of living only in <c>appsettings</c> / env vars.
/// Seeded from the bound <see cref="Notifications.EmailOptions"/> on first access so an
/// existing deployment keeps its configured values until an operator edits them.
/// </summary>
public class EmailSettingsEntity : AuditableEntity
{
    /// <summary>
    /// Well-known fixed primary key — there is only ever one email-settings row.
    /// </summary>
    public static readonly Guid SingletonId = new("e0000000-0000-0000-0000-000000000001");

    /// <summary>"Smtp" — real SMTP. "LogOnly" — write the email to logs (dev/CI default).</summary>
    [Required, MaxLength(16)]
    public string Provider { get; set; } = "LogOnly";

    [MaxLength(256)]
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    [MaxLength(256)]
    public string Username { get; set; } = "";

    /// <summary>AES-256-GCM ciphertext of the SMTP password. Plaintext is never persisted or returned.</summary>
    public string? PasswordEncrypted { get; set; }

    [MaxLength(256)]
    public string FromAddress { get; set; } = "no-reply@stashboard.local";

    [MaxLength(128)]
    public string FromName { get; set; } = "Stashboard";

    /// <summary>Public base URL of the frontend used to build links inside emails.</summary>
    [MaxLength(512)]
    public string AppBaseUrl { get; set; } = "http://localhost:5173";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
