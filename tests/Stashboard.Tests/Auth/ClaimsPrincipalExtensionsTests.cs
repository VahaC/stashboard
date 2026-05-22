using System.Security.Claims;
using Stashboard.Api.Auth;

namespace Stashboard.Tests.Auth;

public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetUserId_ReturnsParsedGuid_FromUidClaim()
    {
        var id = Guid.NewGuid();
        var p = Build(new Claim(StashboardClaims.UserId, id.ToString()));
        Assert.Equal(id, p.GetUserId());
    }

    [Fact]
    public void GetUserId_FallsBackToNameIdentifier()
    {
        var id = Guid.NewGuid();
        var p = Build(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        Assert.Equal(id, p.GetUserId());
    }

    [Fact]
    public void GetUserId_NoClaim_Throws()
    {
        var p = Build();
        Assert.Throws<UnauthorizedAccessException>(() => p.GetUserId());
    }

    [Fact]
    public void GetUserId_NonGuidClaim_Throws()
    {
        var p = Build(new Claim(StashboardClaims.UserId, "not-a-guid"));
        Assert.Throws<UnauthorizedAccessException>(() => p.GetUserId());
    }

    [Fact]
    public void GetEmail_PrefersStashboardClaim()
    {
        var p = Build(
            new Claim(StashboardClaims.Email, "u@x"),
            new Claim(ClaimTypes.Email, "fallback@x"));
        Assert.Equal("u@x", p.GetEmail());
    }

    [Fact]
    public void GetSecurityStamp_ReadsStmpClaim()
    {
        var p = Build(new Claim(StashboardClaims.SecurityStamp, "abc"));
        Assert.Equal("abc", p.GetSecurityStamp());
    }

    private static ClaimsPrincipal Build(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));
}
