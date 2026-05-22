namespace Stashboard.Api.Notifications;

/// <summary>
/// SMTP / email-sender configuration bound from the "Email" section in appsettings.
/// Used only to seed the runtime-editable <see cref="Data.EmailSettingsEntity"/> row on
/// first access; live settings are read from the DB via <see cref="IEmailSettingsService"/>.
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"Smtp" — real SMTP. "LogOnly" — write the email to logs (dev/CI default).</summary>
    public string Provider { get; set; } = "LogOnly";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "no-reply@stashboard.local";
    public string FromName { get; set; } = "Stashboard";

    /// <summary>Public base URL of the frontend used to build links inside emails.</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:5173";
}
