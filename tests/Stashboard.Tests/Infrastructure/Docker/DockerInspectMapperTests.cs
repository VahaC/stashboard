using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V3.1 — unit tests for the env-secret masking heuristic. The full inspect
/// mapper exercises Docker.DotNet types and is covered indirectly by the
/// controller test suite; here we lock down the rule that decides which env
/// vars get their value scrubbed before the inspect payload is returned.
/// </summary>
public class DockerInspectMapperTests
{
    [Theory]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("postgres_password")]
    [InlineData("DB_PASSWD")]
    [InlineData("API_SECRET")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("MY_API_KEY")]
    [InlineData("MYAPIKEY")]
    [InlineData("SSH_PRIVATE_KEY")]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AUTH_TOKEN")]
    [InlineData("DB_CREDENTIAL")]
    public void IsSecretKey_ReturnsTrue_ForKnownSecretKeyShapes(string key)
    {
        Assert.True(DockerInspectMapper.IsSecretKey(key));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("LANG")]
    [InlineData("PUID")]
    [InlineData("TZ")]
    [InlineData("NODE_ENV")]
    [InlineData("DEBUG")]
    [InlineData("")]
    public void IsSecretKey_ReturnsFalse_ForBenignEnvKeys(string key)
    {
        Assert.False(DockerInspectMapper.IsSecretKey(key));
    }

    [Fact]
    public void MapEnvVar_MasksValue_WhenKeyMatchesHeuristic()
    {
        var result = DockerInspectMapper.MapEnvVar("DATABASE_PASSWORD=hunter2");

        Assert.Equal("DATABASE_PASSWORD", result.Key);
        Assert.True(result.Masked);
        Assert.Null(result.Value);
    }

    [Fact]
    public void MapEnvVar_PreservesValue_ForBenignKey()
    {
        var result = DockerInspectMapper.MapEnvVar("TZ=Europe/Kyiv");

        Assert.Equal("TZ", result.Key);
        Assert.False(result.Masked);
        Assert.Equal("Europe/Kyiv", result.Value);
    }

    [Fact]
    public void MapEnvVar_SplitsOnFirstEqualsOnly()
    {
        // Base64 / connection strings often contain `=` themselves; the
        // mapper must not chop those off.
        var result = DockerInspectMapper.MapEnvVar("DATABASE_URL=postgres://u:p@host/db?ssl=true");

        Assert.Equal("DATABASE_URL", result.Key);
        Assert.False(result.Masked);
        Assert.Equal("postgres://u:p@host/db?ssl=true", result.Value);
    }

    [Fact]
    public void MapEnvVar_HandlesKeyWithoutValue()
    {
        var result = DockerInspectMapper.MapEnvVar("EMPTY_FLAG");

        Assert.Equal("EMPTY_FLAG", result.Key);
        Assert.False(result.Masked);
        Assert.Equal(string.Empty, result.Value);
    }
}



