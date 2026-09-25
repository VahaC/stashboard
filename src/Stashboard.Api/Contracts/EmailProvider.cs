namespace Stashboard.Api.Contracts;

/// <summary>
/// How the app sends mail. Serialized by the global string-enum converter as its member
/// name (<c>Smtp</c> / <c>LogOnly</c>) — matching the stored + wire values — so an
/// out-of-range value is rejected by model binding without a regex.
/// </summary>
public enum EmailProvider
{
    /// <summary>Print emails to the server log instead of sending them.</summary>
    LogOnly,
    /// <summary>Send via an SMTP server.</summary>
    Smtp,
}
