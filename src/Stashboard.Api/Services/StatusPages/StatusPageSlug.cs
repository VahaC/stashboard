using System.Text;
using System.Text.RegularExpressions;

namespace Stashboard.Api.Services.StatusPages;

/// <summary>
/// V10.2 — slug helpers for public status pages. A slug is the public URL segment
/// (<c>/status/{slug}</c>) and must be lowercase kebab-case so it's clean in a link and safe
/// to route. <see cref="Slugify"/> derives a candidate from a title; <see cref="IsValid"/>
/// guards user-supplied slugs.
/// </summary>
public static partial class StatusPageSlug
{
    public const int MaxLength = 80;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex ValidPattern();

    /// <summary>True for a non-empty, lowercase kebab-case slug (no leading/trailing/double hyphens).</summary>
    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && ValidPattern().IsMatch(slug);

    /// <summary>
    /// Turn arbitrary text into a slug candidate: lowercase, ASCII-only, spaces/punctuation
    /// collapsed to single hyphens, trimmed. Returns "" when the input has no usable characters
    /// (the caller then falls back to a random slug).
    /// </summary>
    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        var lastWasHyphen = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && sb.Length > 0)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('-') : slug;
    }

    /// <summary>A short random slug used when a title slugifies to nothing (e.g. all emoji).</summary>
    public static string Random() => "status-" + Guid.NewGuid().ToString("N")[..8];
}
