using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Stashboard.Api.Notifications;

/// <summary>
/// Email sender driven by the runtime-editable <see cref="IEmailSettingsService"/> instead of
/// a startup-bound provider. On each send it resolves the current settings: <c>Provider=Smtp</c>
/// with a configured host sends via MailKit, otherwise the message is written to the logger so
/// confirmation/reset links can be copied from the console (dev/CI default — never sends).
/// </summary>
public sealed class DbEmailSender(IEmailSettingsService settings, ILogger<DbEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var opt = await settings.GetResolvedAsync(cancellationToken);

        if (!string.Equals(opt.Provider, "Smtp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(opt.Host))
        {
            logger.LogInformation(
                "[DEV-EMAIL] To: {To}\nSubject: {Subject}\n--- TEXT ---\n{TextBody}\n--- END ---",
                message.To, message.Subject, message.TextBody);
            return;
        }

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(opt.FromName, opt.FromAddress));
        msg.To.Add(MailboxAddress.Parse(message.To));
        msg.Subject = message.Subject;
        msg.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        var secure = opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(opt.Host, opt.Port, secure, cancellationToken);
        if (!string.IsNullOrEmpty(opt.Username))
            await client.AuthenticateAsync(opt.Username, opt.Password, cancellationToken);
        await client.SendAsync(msg, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        logger.LogInformation("Email sent to {To} subject={Subject}", message.To, message.Subject);
    }
}
