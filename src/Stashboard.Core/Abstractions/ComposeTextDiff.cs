namespace Stashboard.Core.Abstractions;

/// <summary>V7.6 — kind of a single line in a pre-save Compose diff.</summary>
public enum ComposeDiffLineType
{
    /// <summary>Unchanged line, present in both the on-disk file and the proposed text.</summary>
    Context = 0,

    /// <summary>Line present only in the proposed text (added by the edit).</summary>
    Added = 1,

    /// <summary>Line present only in the on-disk file (removed by the edit).</summary>
    Removed = 2,
}

/// <summary>
/// V7.6 — one line of a textual Compose diff. <see cref="OldLine"/> /
/// <see cref="NewLine"/> are 1-based line numbers in the on-disk / proposed
/// file respectively, or <c>null</c> when the line does not exist on that side.
/// </summary>
public sealed record ComposeDiffLine(ComposeDiffLineType Type, string Text, int? OldLine, int? NewLine);

/// <summary>
/// V7.6 — pure line-level diff between the on-disk Compose file and the proposed
/// replacement, for the "see what changes before you save" confirm step. Uses an
/// LCS so unchanged lines line up as context and only the genuine inserts /
/// deletes are flagged. No file or Docker access — text in, diff out — so it is
/// trivially unit-testable.
/// </summary>
public static class ComposeTextDiff
{
    /// <summary>
    /// Computes the diff. Both inputs are split on <c>\n</c> with a trailing
    /// <c>\r</c> stripped from each line so a CRLF file diffs cleanly against an
    /// LF edit. The result preserves file order: context, added and removed
    /// lines are interleaved exactly as they appear when reading top to bottom.
    /// </summary>
    public static IReadOnlyList<ComposeDiffLine> Compute(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        // Classic LCS length table over the two line sequences.
        var n = oldLines.Length;
        var m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<ComposeDiffLine>(Math.Max(n, m));
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (string.Equals(oldLines[a], newLines[b], StringComparison.Ordinal))
            {
                result.Add(new ComposeDiffLine(ComposeDiffLineType.Context, oldLines[a], a + 1, b + 1));
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                result.Add(new ComposeDiffLine(ComposeDiffLineType.Removed, oldLines[a], a + 1, null));
                a++;
            }
            else
            {
                result.Add(new ComposeDiffLine(ComposeDiffLineType.Added, newLines[b], null, b + 1));
                b++;
            }
        }
        while (a < n) result.Add(new ComposeDiffLine(ComposeDiffLineType.Removed, oldLines[a], ++a, null));
        while (b < m) result.Add(new ComposeDiffLine(ComposeDiffLineType.Added, newLines[b], null, ++b));

        return result;
    }

    /// <summary>Whether the two texts differ at all (cheap pre-check the caller
    /// uses to short-circuit a no-op save).</summary>
    public static bool HasChanges(string oldText, string newText) =>
        !string.Equals(oldText, newText, StringComparison.Ordinal);

    private static string[] SplitLines(string text)
    {
        // A trailing newline would otherwise yield a phantom empty final line on
        // one side only; trim a single trailing \n (and its \r) so the two sides
        // align on the last real line.
        var trimmed = text.EndsWith('\n') ? text[..^1] : text;
        if (trimmed.EndsWith('\r')) trimmed = trimmed[..^1];
        if (trimmed.Length == 0) return [];
        var lines = trimmed.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        return lines;
    }
}
