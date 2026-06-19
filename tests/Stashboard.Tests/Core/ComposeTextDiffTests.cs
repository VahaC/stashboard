using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.CoreUnit;

/// <summary>
/// V7.6 — unit tests for <see cref="ComposeTextDiff"/>: the LCS line diff that
/// powers the pre-save confirm step. Verifies context/added/removed
/// classification, line-number tracking, CRLF normalisation and the trailing
/// newline handling.
/// </summary>
public class ComposeTextDiffTests
{
    [Fact]
    public void Compute_IdenticalText_IsAllContext()
    {
        var text = "services:\n  web:\n    image: nginx\n";
        var diff = ComposeTextDiff.Compute(text, text);

        Assert.All(diff, l => Assert.Equal(ComposeDiffLineType.Context, l.Type));
        Assert.False(ComposeTextDiff.HasChanges(text, text));
        Assert.Equal(3, diff.Count);
    }

    [Fact]
    public void Compute_ChangedLine_YieldsRemovedThenAdded()
    {
        var oldText = "services:\n  web:\n    image: nginx:1.25\n";
        var newText = "services:\n  web:\n    image: nginx:1.27\n";

        var diff = ComposeTextDiff.Compute(oldText, newText);

        var removed = Assert.Single(diff, l => l.Type == ComposeDiffLineType.Removed);
        var added = Assert.Single(diff, l => l.Type == ComposeDiffLineType.Added);
        Assert.Equal("    image: nginx:1.25", removed.Text);
        Assert.Equal(3, removed.OldLine);
        Assert.Null(removed.NewLine);
        Assert.Equal("    image: nginx:1.27", added.Text);
        Assert.Equal(3, added.NewLine);
        Assert.Null(added.OldLine);
    }

    [Fact]
    public void Compute_AddedLines_AreFlaggedAdded_WithContextPreserved()
    {
        var oldText = "services:\n  web:\n    image: nginx\n";
        var newText = "services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n";

        var diff = ComposeTextDiff.Compute(oldText, newText);

        Assert.Equal(3, diff.Count(l => l.Type == ComposeDiffLineType.Context));
        var added = diff.Where(l => l.Type == ComposeDiffLineType.Added).ToList();
        Assert.Equal(2, added.Count);
        Assert.Equal("  db:", added[0].Text);
        Assert.Equal("    image: postgres", added[1].Text);
    }

    [Fact]
    public void Compute_CrlfVsLf_TreatedAsEqual()
    {
        var oldText = "services:\r\n  web:\r\n    image: nginx\r\n";
        var newText = "services:\n  web:\n    image: nginx\n";

        var diff = ComposeTextDiff.Compute(oldText, newText);
        Assert.All(diff, l => Assert.Equal(ComposeDiffLineType.Context, l.Type));
    }

    [Fact]
    public void Compute_EmptyToContent_IsAllAdded()
    {
        var diff = ComposeTextDiff.Compute("", "a\nb\n");
        Assert.Equal(2, diff.Count);
        Assert.All(diff, l => Assert.Equal(ComposeDiffLineType.Added, l.Type));
        Assert.Equal(1, diff[0].NewLine);
        Assert.Equal(2, diff[1].NewLine);
    }
}
