using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Covers every parsing case enumerated in ROADMAP.md §5.1 plus
/// the edge-cases the parser has to defend against.
///
/// Pattern: pure unit tests against the real <see cref="ImageReferenceParser"/> —
/// no mocking, no DI. The class has no dependencies so this is the right level.
/// </summary>
public class ImageReferenceParserTests
{
    private readonly IImageReferenceParser _parser = new ImageReferenceParser();

    // ── Docker Hub: library shorthand ────────────────────────────────────────

    [Fact]
    public void Parse_SingleSegmentName_ExpandsToDockerHubLibrary()
    {
        var result = _parser.Parse("nginx");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("library/nginx", result.Repository);
        Assert.Equal("latest", result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_SingleSegmentNameWithTag_AppliesLibraryPrefix()
    {
        var result = _parser.Parse("nginx:1.27");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("library/nginx", result.Repository);
        Assert.Equal("1.27", result.Tag);
    }

    [Fact]
    public void Parse_ExplicitLibraryNamespace_PreservesLibraryPrefix()
    {
        var result = _parser.Parse("library/postgres:16-alpine");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("library/postgres", result.Repository);
        Assert.Equal("16-alpine", result.Tag);
    }

    [Fact]
    public void Parse_NamespacedHubImage_KeepsCustomNamespace()
    {
        var result = _parser.Parse("linuxserver/sonarr");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("linuxserver/sonarr", result.Repository);
        Assert.Equal("latest", result.Tag);
    }

    [Fact]
    public void Parse_NamespacedHubImageWithTag_KeepsBoth()
    {
        var result = _parser.Parse("linuxserver/sonarr:4.0");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("linuxserver/sonarr", result.Repository);
        Assert.Equal("4.0", result.Tag);
    }

    // ── GHCR (and any registry containing a dot) ─────────────────────────────

    [Fact]
    public void Parse_GhcrImage_ExtractsRegistryAndRepoAndTag()
    {
        var result = _parser.Parse("ghcr.io/owner/repo:v1.2.3");

        Assert.Equal("ghcr.io", result.RegistryHost);
        Assert.Equal("owner/repo", result.Repository);
        Assert.Equal("v1.2.3", result.Tag);
    }

    [Fact]
    public void Parse_GhcrImageWithoutTag_DefaultsToLatest()
    {
        var result = _parser.Parse("ghcr.io/owner/repo");

        Assert.Equal("ghcr.io", result.RegistryHost);
        Assert.Equal("owner/repo", result.Repository);
        Assert.Equal("latest", result.Tag);
    }

    [Fact]
    public void Parse_GhcrImageWithDeepPath_PreservesAllSegments()
    {
        var result = _parser.Parse("ghcr.io/group/project/service:edge");

        Assert.Equal("ghcr.io", result.RegistryHost);
        Assert.Equal("group/project/service", result.Repository);
        Assert.Equal("edge", result.Tag);
    }

    // ── Self-hosted registries: port + 'localhost' ───────────────────────────

    [Fact]
    public void Parse_RegistryWithPort_DoesNotConfuseColonForTag()
    {
        var result = _parser.Parse("registry:5000/foo:1.0");

        Assert.Equal("registry:5000", result.RegistryHost);
        Assert.Equal("foo", result.Repository);
        Assert.Equal("1.0", result.Tag);
    }

    [Fact]
    public void Parse_LocalhostRegistry_IsRecognisedWithoutDot()
    {
        var result = _parser.Parse("localhost:5000/foo");

        Assert.Equal("localhost:5000", result.RegistryHost);
        Assert.Equal("foo", result.Repository);
        Assert.Equal("latest", result.Tag);
    }

    [Fact]
    public void Parse_LocalhostRegistryUppercase_IsRecognised()
    {
        var result = _parser.Parse("Localhost:5000/foo");

        Assert.Equal("Localhost:5000", result.RegistryHost);
        Assert.Equal("foo", result.Repository);
    }

    // ── Digest references ────────────────────────────────────────────────────

    [Fact]
    public void Parse_DigestWithoutTag_DefaultsTagToLatest()
    {
        var result = _parser.Parse("nginx@sha256:abc123def456abc123def456abc123def456abc123def456abc123def456abcd");

        Assert.Equal("docker.io", result.RegistryHost);
        Assert.Equal("library/nginx", result.Repository);
        Assert.Equal("latest", result.Tag);
        Assert.Equal("sha256:abc123def456abc123def456abc123def456abc123def456abc123def456abcd", result.Digest);
    }

    [Fact]
    public void Parse_TagAndDigest_BothPreserved()
    {
        var result = _parser.Parse("nginx:1.27@sha256:abc123def456abc123def456abc123def456abc123def456abc123def456abcd");

        Assert.Equal("library/nginx", result.Repository);
        Assert.Equal("1.27", result.Tag);
        Assert.Equal("sha256:abc123def456abc123def456abc123def456abc123def456abc123def456abcd", result.Digest);
    }

    [Fact]
    public void Parse_GhcrWithDigest_PreservesEverything()
    {
        var result = _parser.Parse("ghcr.io/owner/repo:v1@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.Equal("ghcr.io", result.RegistryHost);
        Assert.Equal("owner/repo", result.Repository);
        Assert.Equal("v1", result.Tag);
        Assert.StartsWith("sha256:", result.Digest);
    }

    // ── Whitespace handling ──────────────────────────────────────────────────

    [Fact]
    public void Parse_WrappingWhitespace_IsTrimmed()
    {
        var result = _parser.Parse("   nginx:1.27   ");

        Assert.Equal("library/nginx", result.Repository);
        Assert.Equal("1.27", result.Tag);
    }

    // ── Invalid input ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@sha256:abc")]                                  // digest with no name
    [InlineData("nginx@")]                                        // empty digest
    [InlineData("nginx@sha256")]                                  // digest missing hex
    [InlineData("nginx@sha256:")]                                 // digest with empty hex
    [InlineData("nginx@noColonDigest")]                           // digest must have algo:hex
    [InlineData("nginx:")]                                        // empty tag
    [InlineData(":1.27")]                                         // empty name
    [InlineData("ghcr.io/owner/repo:")]                           // empty tag
    [InlineData("ghcr.io//repo:tag")]                             // double slash
    [InlineData("/nginx")]                                        // leading slash
    [InlineData("nginx/")]                                        // trailing slash
    public void Parse_InvalidInput_Throws(string input)
    {
        Assert.Throws<FormatException>(() => _parser.Parse(input));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<FormatException>(() => _parser.Parse(null!));
    }

    // ── TryParse mirror ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndPopulatesResult()
    {
        var success = _parser.TryParse("ghcr.io/owner/repo:tag", out var result);

        Assert.True(success);
        Assert.Equal("ghcr.io", result.RegistryHost);
        Assert.Equal("owner/repo", result.Repository);
        Assert.Equal("tag", result.Tag);
    }

    [Fact]
    public void TryParse_InvalidInput_ReturnsFalseWithoutThrowing()
    {
        var success = _parser.TryParse("", out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    // ── V2.4: AWS ECR host detection ─────────────────────────────────────────

    [Theory]
    [InlineData("123456789012.dkr.ecr.eu-central-1.amazonaws.com", "eu-central-1")]
    [InlineData("999999999999.dkr.ecr.us-east-1.amazonaws.com", "us-east-1")]
    [InlineData("111122223333.dkr.ecr.ap-southeast-2.amazonaws.com", "ap-southeast-2")]
    public void TryExtractEcrRegion_PrivateEcrHost_ReturnsEmbeddedRegion(string host, string expectedRegion)
    {
        Assert.Equal(expectedRegion, ImageReferenceParser.TryExtractEcrRegion(host));
        Assert.True(ImageReferenceParser.LooksLikeEcrHost(host));
    }

    [Theory]
    [InlineData("docker.io")]
    [InlineData("ghcr.io")]
    [InlineData("registry.gitlab.com")]
    [InlineData("public.ecr.aws")]                                  // public ECR is regionless
    [InlineData("12345.dkr.ecr.eu-central-1.amazonaws.com")]        // account id wrong length
    [InlineData("123456789012.dkr.ecr.eu_central_1.amazonaws.com")] // underscore not allowed in region
    public void TryExtractEcrRegion_NonPrivateEcr_ReturnsNull(string host)
    {
        Assert.Null(ImageReferenceParser.TryExtractEcrRegion(host));
        Assert.False(ImageReferenceParser.LooksLikeEcrHost(host));
    }

    [Fact]
    public void Parse_PrivateEcrReference_KeepsHostnameAndRepositoryIntact()
    {
        var result = _parser.Parse("123456789012.dkr.ecr.eu-central-1.amazonaws.com/my-app:1.2.3");

        Assert.Equal("123456789012.dkr.ecr.eu-central-1.amazonaws.com", result.RegistryHost);
        Assert.Equal("my-app", result.Repository);
        Assert.Equal("1.2.3", result.Tag);
    }
}
