namespace Stashboard.Api.Notifications;

/// <summary>
/// Composes account-related email links and dispatches the corresponding email messages.
/// Pulled out of <c>AccountController</c> per <c>REQUIREMENTS.md</c> rule #1 (SRP / SOLID).
///
/// Pattern: <b>Service Layer / Facade</b>. Hides the link-composition + template + sender
/// orchestration behind a small intent-revealing API.
/// </summary>
public interface IAccountNotificationService
{
    Task SendEmailConfirmationAsync(string email, string token, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(string email, string token, CancellationToken cancellationToken);
    Task SendEmailChangeConfirmationAsync(string newEmail, string token, CancellationToken cancellationToken);
}
