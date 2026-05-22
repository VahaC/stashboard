using Stashboard.Api.Contracts;

namespace Stashboard.Api.Notifications;

/// <summary>
/// Fully-resolved, decrypted email settings used by the sender and the account
/// notification service at send-time. <see cref="Password"/> is plaintext — never log it.
/// </summary>
public sealed record ResolvedEmailSettings(
    string Provider,
    string Host,
    int Port,
    bool UseStartTls,
    string Username,
    string Password,
    string FromAddress,
    string FromName,
    string AppBaseUrl);

/// <summary>
/// Reads and writes the single app-wide email-settings row. The row is created on first
/// access from the bound <see cref="EmailOptions"/> so an existing deployment keeps its
/// configured values until an operator edits them in the UI.
/// </summary>
public interface IEmailSettingsService
{
    /// <summary>Loads (creating if needed) the settings and decrypts the password for sending.</summary>
    Task<ResolvedEmailSettings> GetResolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>Masked view for the API — password is never returned, only a presence flag.</summary>
    Task<EmailSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists edited settings, encrypting the password (tri-state: keep / set / clear).</summary>
    Task UpdateAsync(UpdateEmailSettingsRequest request, CancellationToken cancellationToken = default);
}
