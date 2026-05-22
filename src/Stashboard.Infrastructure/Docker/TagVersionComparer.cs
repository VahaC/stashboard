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

    private static readonly Regex VersionShape = new(
        @"^v?(\d+(?:\.\d+)*)(?:[-+](.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (xNums, xPre) = TryParse(x);
        var (yNums, yPre) = TryParse(y);

        // If either side isn't semver-shaped, fall back to lexicographic.
        if (xNums is null || yNums is null)
            return string.CompareOrdinal(x, y);

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
        return string.CompareOrdinal(xPre, yPre);
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
}
