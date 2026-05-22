using System.Net;

namespace Stashboard.Api.Notifications;

/// <summary>
/// Trivial inline templates. Keeps this dependency-free; replace with Razor or a templating
/// engine if email content grows substantially.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage EmailConfirmation(string toEmail, string link)
    {
        var safe = WebUtility.HtmlEncode(link);
        var html = $"""
            <p>Welcome to Stashboard!</p>
            <p>Confirm your email address by clicking the link below:</p>
            <p><a href="{safe}">Confirm email</a></p>
            <p>If you didn't sign up, ignore this message.</p>
            """;
        var text = $"Welcome to Stashboard!\n\nConfirm your email: {link}\n\nIf you didn't sign up, ignore this message.";
        return new EmailMessage(toEmail, "Confirm your Stashboard email", html, text);
    }

    public static EmailMessage PasswordReset(string toEmail, string link)
    {
        var safe = WebUtility.HtmlEncode(link);
        var html = $"""
            <p>We received a password reset request for your Stashboard account.</p>
            <p><a href="{safe}">Reset password</a></p>
            <p>The link expires soon. If you didn't request a reset, ignore this email.</p>
            """;
        var text = $"Reset your Stashboard password: {link}\n\nIf you didn't request a reset, ignore this email.";
        return new EmailMessage(toEmail, "Reset your Stashboard password", html, text);
    }

    public static EmailMessage EmailChangeConfirmation(string toEmail, string link)
    {
        var safe = WebUtility.HtmlEncode(link);
        var html = $"""
            <p>Confirm this email address as the new login for your Stashboard account:</p>
            <p><a href="{safe}">Confirm new email</a></p>
            <p>If you didn't request this change, ignore this email and your old address will remain active.</p>
            """;
        var text = $"Confirm new email for Stashboard: {link}";
        return new EmailMessage(toEmail, "Confirm your new Stashboard email", html, text);
    }

    public static EmailMessage DockerUpdateAvailable(
        string toEmail,
        string serviceName,
        string imageReference,
        string containerName,
        string? currentDigest,
        string latestDigest,
        // V2.3 — optional release-notes link (GHCR images with a matching
        // GitHub release). Both null on non-GHCR or when enrichment couldn't
        // find a release for the tag.
        string? releaseNotesUrl = null)
    {
        var safeName = WebUtility.HtmlEncode(serviceName);
        var safeImage = WebUtility.HtmlEncode(imageReference);
        var safeContainer = WebUtility.HtmlEncode(containerName);
        var safeCurrent = WebUtility.HtmlEncode(currentDigest ?? "unknown");
        var safeLatest = WebUtility.HtmlEncode(latestDigest ?? "unknown");

        var htmlReleaseRow = string.IsNullOrEmpty(releaseNotesUrl)
            ? string.Empty
            : $"""<p>Release notes: <a href="{WebUtility.HtmlEncode(releaseNotesUrl)}">{WebUtility.HtmlEncode(releaseNotesUrl)}</a></p>""";
        var textReleaseLine = string.IsNullOrEmpty(releaseNotesUrl)
            ? string.Empty
            : $"\nRelease notes: {releaseNotesUrl}\n";

        var html = $"""
            <p>A newer image is available for <strong>{safeName}</strong>.</p>
            <table cellpadding="4" style="border-collapse:collapse;font-family:monospace;">
              <tr><td>Image:</td><td>{safeImage}</td></tr>
              <tr><td>Container:</td><td>{safeContainer}</td></tr>
              <tr><td>Current digest:</td><td>{safeCurrent}</td></tr>
              <tr><td>Latest digest:</td><td>{safeLatest}</td></tr>
            </table>
            {htmlReleaseRow}
            <p>Pull and recreate the container at your convenience. Stashboard only checks — it never updates running containers automatically.</p>
            """;

        var text = $"""
            A newer image is available for {serviceName}.

              Image:          {imageReference}
              Container:      {containerName}
              Current digest: {currentDigest ?? "unknown"}
              Latest digest:  {latestDigest ?? "unknown"}
            {textReleaseLine}
            Pull and recreate the container at your convenience.
            Stashboard only checks - it never updates running containers automatically.
            """;

        return new EmailMessage(toEmail, $"[Stashboard] Update available for {serviceName}", html, text);
    }

    private static string? ShortenDigest(string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return null;
        var colon = digest.IndexOf(':');
        if (colon < 0 || digest.Length <= colon + 13) return digest;
        // sha256:abcdef12... — show 12 hex chars after the colon.
        return digest[..(colon + 13)] + "...";
    }
}
