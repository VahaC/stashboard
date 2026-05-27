using System.Text.RegularExpressions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Orders tags so the "newest" one wins under <c>OrderByDescending</c>. Used
/// by the V2.1 tag-pattern filter when picking which matching tag to treat
/// as "latest".
/// </summary>
/// <remarks>
/// Semver-ish tags (<c>1.2.3</c>, <c>v1.2.3</c>, <c>v1.2.3-rc.4</c>) compare
/// component-wise; non-numeric / unparseable tags fall back to ordinal string
/// compare. Pre-release tags (with a hyphen suffix) rank below their plain
/// counterpart so <c>v1.2.3</c> beats <c>v1.2.3-rc1</c> — same rule semver.org
/// spells out. Trailing components missing on one side count as zero, so
/// <c>v1.2</c> compares equal to <c>v1.2.0</c>.
/// </remarks>
public sealed class TagVersionComparer : IComparer<string>
{
    public static readonly TagVersionComparer Instance = new();

    private TagVersionComparer() { }

    // Group 1: dotted numeric core. Group 2: optional pre-release (after the
    // first '-'). Build metadata (after '+') is captured loosely and discarded
    // — semver says it never affects precedence.
    private static readonly Regex VersionShape = new(
        @"^v?(\d+(?:\.\d+)*)(?:-([^+]*))?(?:\+.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (xNums, xPre) = TryParse(x);
        var (yNums, yPre) = TryParse(y);

        // A semver-shaped tag always outranks a non-semver one, regardless of
        // how the two strings compare ordinally — otherwise a junk tag like
        // `zzz-experimental` (ordinally above `v2.0.0`) could be picked as the
        // "latest matching tag". Two non-semver tags still fall back to ordinal.
        if (xNums is null && yNums is null) return string.CompareOrdinal(x, y);
        if (xNums is null) return -1;
        if (yNums is null) return 1;

        var maxLen = Math.Max(xNums.Length, yNums.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var a = i < xNums.Length ? xNums[i] : 0;
            var b = i < yNums.Length ? yNums[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        // Equal numeric prefix: a plain tag (no -prerelease) outranks one with
        // a prerelease suffix (semver §11).
        if (xPre is null && yPre is null) return 0;
        if (xPre is null) return 1;   // x is the plain release → greater
        if (yPre is null) return -1;
        return ComparePreRelease(xPre, yPre);
    }

    private static (int[]? Numbers, string? PreRelease) TryParse(string tag)
    {
        var match = VersionShape.Match(tag);
        if (!match.Success) return (null, null);

        var numberParts = match.Groups[1].Value.Split('.');
        var numbers = new int[numberParts.Length];
        for (var i = 0; i < numberParts.Length; i++)
        {
            if (!int.TryParse(numberParts[i], out var n) || n < 0) return (null, null);
            numbers[i] = n;
        }

        var pre = match.Groups[2].Success ? match.Groups[2].Value : null;
        return (numbers, pre);
    }

    /// <summary>
    /// Compares two pre-release strings per semver §11: split on '.', then
    /// compare identifiers left-to-right. All-numeric identifiers compare
    /// numerically and rank below alphanumeric ones; a larger set of fields
    /// wins when every preceding identifier is equal (alpha &lt; alpha.1).
    /// </summary>
    private static int ComparePreRelease(string x, string y)
    {
        var xs = x.Split('.');
        var ys = y.Split('.');
        var max = Math.Max(xs.Length, ys.Length);
        for (var i = 0; i < max; i++)
        {
            if (i >= xs.Length) return -1; // x ran out of fields → lower
            if (i >= ys.Length) return 1;

            var xNumeric = int.TryParse(xs[i], out var xn) && xs[i].All(char.IsDigit);
            var yNumeric = int.TryParse(ys[i], out var yn) && ys[i].All(char.IsDigit);

            if (xNumeric && yNumeric)
            {
                if (xn != yn) return xn.CompareTo(yn);
            }
            else if (xNumeric) return -1; // numeric ranks below alphanumeric
            else if (yNumeric) return 1;
            else
            {
                var cmp = string.CompareOrdinal(xs[i], ys[i]);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }
}
