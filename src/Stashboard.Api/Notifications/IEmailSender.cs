namespace Stashboard.Api.Notifications;

/// <summary>
/// Abstraction over outbound email. The single implementation <see cref="DbEmailSender"/> resolves
/// the runtime-editable email settings per send: real SMTP when configured, otherwise it writes the
/// message to ILogger so the link can be copied from the console without a real SMTP server.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
