using Stashboard.Infrastructure.Security;

namespace Stashboard.Tests.Infrastructure.Security;

public sealed class PersistedSecretProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stashboard-secrets-{Guid.NewGuid():N}");

    [Fact]
    public void GetOrCreate_GeneratesAndPersists_WhenFileAbsent()
    {
        var (value, created) = PersistedSecretProvider.GetOrCreate(_dir, "encryption.key", () => "generated-value");

        Assert.True(created);
        Assert.Equal("generated-value", value);
        Assert.Equal("generated-value", File.ReadAllText(Path.Combine(_dir, "encryption.key")));
    }

    [Fact]
    public void GetOrCreate_ReturnsSameValue_OnSubsequentCalls()
    {
        var calls = 0;
        string Generate() { calls++; return $"value-{calls}"; }

        var first = PersistedSecretProvider.GetOrCreate(_dir, "jwt.secret", Generate);
        var second = PersistedSecretProvider.GetOrCreate(_dir, "jwt.secret", Generate);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, calls); // generator not invoked the second time
    }

    [Fact]
    public void GetOrCreate_Regenerates_WhenFileIsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "encryption.key");
        File.WriteAllText(path, "   ");

        var (value, created) = PersistedSecretProvider.GetOrCreate(_dir, "encryption.key", () => "fresh");

        Assert.True(created);
        Assert.Equal("fresh", value);
        Assert.Equal("fresh", File.ReadAllText(path));
    }

    [Fact]
    public void GetOrCreate_TrimsTrailingWhitespace_OnRead()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "jwt.secret"), "secret-value\n");

        var (value, created) = PersistedSecretProvider.GetOrCreate(_dir, "jwt.secret", () => "should-not-be-used");

        Assert.False(created);
        Assert.Equal("secret-value", value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}



