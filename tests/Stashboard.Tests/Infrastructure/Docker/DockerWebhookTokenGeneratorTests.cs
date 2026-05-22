using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.6 — sanity tests for the CSPRNG-backed token generator. We don't try
/// to assert randomness (that's the CSPRNG's job); we just pin the shape
/// the rest of the system relies on: 64 lowercase hex chars, distinct on
/// every call.
/// </summary>
public class DockerWebhookTokenGeneratorTests
{
    private readonly DockerWebhookTokenGenerator _sut = new();

    [Fact]
    public void Generate_Returns64LowercaseHexCharacters()
    {
        var token = _sut.Generate();

        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public void Generate_TwoCalls_ProduceDistinctTokens()
    {
        var a = _sut.Generate();
        var b = _sut.Generate();
        Assert.NotEqual(a, b);
    }
}
