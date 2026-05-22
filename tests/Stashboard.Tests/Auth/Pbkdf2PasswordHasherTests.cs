using Stashboard.Api.Auth;

namespace Stashboard.Tests.Auth;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _sut = new();

    [Fact]
    public void Hash_Format_StartsWithAlgorithmAndIterations()
    {
        var hash = _sut.Hash("hunter2");

        var parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal(Pbkdf2PasswordHasher.Iterations.ToString(), parts[1]);
        Assert.True(Convert.FromBase64String(parts[2]).Length == Pbkdf2PasswordHasher.SaltBytes);
        Assert.True(Convert.FromBase64String(parts[3]).Length == Pbkdf2PasswordHasher.HashBytes);
    }

    [Fact]
    public void Hash_TwoCallsForSamePassword_ProduceDifferentHashes()
    {
        var a = _sut.Hash("same");
        var b = _sut.Hash("same");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = _sut.Hash("correct horse battery staple");
        Assert.True(_sut.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = _sut.Hash("right");
        Assert.False(_sut.Verify("wrong", hash));
    }

    [Fact]
    public void Verify_EmptyHash_ReturnsFalse() => Assert.False(_sut.Verify("x", ""));

    [Fact]
    public void Verify_EmptyPassword_ReturnsFalse()
    {
        var hash = _sut.Hash("x");
        Assert.False(_sut.Verify("", hash));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("$$$")]
    [InlineData("pbkdf2-sha256$abc$saltbase64$hashbase64")]
    [InlineData("bcrypt$1$aaaa$bbbb")]
    [InlineData("pbkdf2-sha256$1000$!!!notbase64$bbbb")]
    public void Verify_MalformedHash_ReturnsFalse(string hash)
    {
        Assert.False(_sut.Verify("any", hash));
    }

    [Fact]
    public void Hash_NullPassword_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Hash(null!));
    }
}
