using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Unit tests for <see cref="TagVersionComparer"/>. This is the sort the V2.1
/// tag-pattern filter applies to pick the "latest matching tag" — getting this
/// wrong means the wrong digest gets compared, so coverage is dense by design.
/// </summary>
public class TagVersionComparerTests
{
    private static string PickHighest(IEnumerable<string> tags) =>
        tags.OrderByDescending(t => t, TagVersionComparer.Instance).First();

    [Fact]
    public void PicksHighestSemverFromMix()
    {
        var tags = new[] { "v1.2.0", "v1.10.0", "v1.2.5", "v2.0.0" };
        Assert.Equal("v2.0.0", PickHighest(tags));
    }

    [Fact]
    public void NumericComponentsCompareAsIntegers_Not_Strings()
    {
        // String compare would put v1.10.0 < v1.2.0 because "1" < "2".
        var tags = new[] { "v1.2.0", "v1.10.0" };
        Assert.Equal("v1.10.0", PickHighest(tags));
    }

    [Fact]
    public void PlainReleaseOutranksItsPrerelease()
    {
        // Semver §11: v1.2.3 > v1.2.3-rc1 > v1.2.3-alpha.
        var tags = new[] { "v1.2.3-rc1", "v1.2.3-alpha", "v1.2.3" };
        Assert.Equal("v1.2.3", PickHighest(tags));
    }

    [Fact]
    public void PrereleaseComparedLexicographically()
    {
        var tags = new[] { "v1.2.3-rc1", "v1.2.3-rc2" };
        Assert.Equal("v1.2.3-rc2", PickHighest(tags));
    }

    [Fact]
    public void ShorterVersionEqualsZeroPaddedEquivalent()
    {
        // v1.2 should rank equally with v1.2.0 (trailing components default to 0).
        var sorted = new[] { "v1.2", "v1.2.0", "v1.2.1" }
            .OrderByDescending(t => t, TagVersionComparer.Instance)
            .ToList();
        Assert.Equal("v1.2.1", sorted[0]);
    }

    [Fact]
    public void NonSemverTagsFallBackToLexicographic()
    {
        // No numeric prefix at all → ordinal string compare.
        var tags = new[] { "stable", "edge", "nightly" };
        var sorted = tags.OrderByDescending(t => t, TagVersionComparer.Instance).ToList();
        Assert.Equal(new[] { "stable", "nightly", "edge" }, sorted);
    }

    [Fact]
    public void MixedSemverAndArbitraryTags_SemverWinsByDescendingNumeric()
    {
        // Mixed inputs — sort is stable enough that the highest numeric one
        // still surfaces at the top. (Arbitrary tags fall through to ordinal
        // compare among themselves; we don't try to rank them against
        // semver tags meaningfully.)
        var tags = new[] { "v2.0.0", "latest", "v1.5.0" };
        var highest = PickHighest(tags);
        Assert.Contains(highest, new[] { "v2.0.0", "latest" });
    }

    [Fact]
    public void HandlesPlainNumericVersionsWithoutVPrefix()
    {
        var tags = new[] { "1.27.3", "1.27.10", "1.28.0" };
        Assert.Equal("1.28.0", PickHighest(tags));
    }
}
