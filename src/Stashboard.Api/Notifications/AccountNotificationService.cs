using Microsoft.AspNetCore.Http;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IAccountNotificationService"/>
public sealed class AccountNotificationService(
    IEmailSender emailSender,
    IEmailSettingsService emailSettings,
    IHttpContextAccessor httpContextAccessor) : IAccountNotificationService
{
    public async Task SendEmailConfirmationAsync(string email, string token, CancellationToken cancellationToken)
    {
        var link = await BuildLinkAsync("/confirm-email", cancellationToken, ("email", email), ("token", token));
        await emailSender.SendAsync(EmailTemplates.EmailConfirmation(email, link), cancellationToken);
    }

    public async Task SendPasswordResetAsync(string email, string token, CancellationToken cancellationToken)
    {
        var link = await BuildLinkAsync("/reset-password", cancellationToken, ("email", email), ("token", token));
        await emailSender.SendAsync(EmailTemplates.PasswordReset(email, link), cancellationToken);
    }

    public async Task SendEmailChangeConfirmationAsync(string newEmail, string token, CancellationToken cancellationToken)
    {
        var link = await BuildLinkAsync("/confirm-email-change", cancellationToken, ("token", token));
        await emailSender.SendAsync(EmailTemplates.EmailChangeConfirmation(newEmail, link), cancellationToken);
    }

    private async Task<string> BuildLinkAsync(string path, CancellationToken cancellationToken, params (string Key, string Value)[] queryParameters)
    {
        var settings = await emailSettings.GetResolvedAsync(cancellationToken);
        var baseUrl = ResolveBaseUrl(settings.AppBaseUrl);
        var query = string.Join('&', queryParameters
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{baseUrl}{path}?{query}";
    }

    private string ResolveBaseUrl(string configuredBaseUrl)
    {
        if (TryGetRequestBaseUrl(out var requestBaseUrl) && IsLocalhostUrl(configuredBaseUrl))
            return requestBaseUrl;

        return configuredBaseUrl.TrimEnd('/');
    }

    private bool TryGetRequestBaseUrl(out string baseUrl)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null || !request.Host.HasValue)
        {
            baseUrl = string.Empty;
            return false;
        }

        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scheme))
            scheme = request.Scheme;

        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(host))
            host = request.Host.Value;

        host = host.Split(',')[0].Trim();
        baseUrl = $"{scheme}://{host}".TrimEnd('/');
        return true;
    }

    private static bool IsLocalhostUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        return uri.IsLoopback;
    }
}
