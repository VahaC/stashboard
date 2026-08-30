using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

/// <summary>
/// End-to-end tests of the PAT authentication pipeline (scheme selector + scope policy + deny
/// filter) over a real in-memory ASP.NET host. Covers the acceptance behaviour from the spec:
/// a PAT authenticates as its owner, read scope is 403'd on mutations, expired/revoked tokens are
/// rejected immediately, JWTs still work, last-used is stamped, and PATs are refused on JWT-only
/// surfaces.
/// </summary>
public class PersonalAccessTokenPipelineTests
{
    private static HttpRequestMessage Get(string token) => Bearer(HttpMethod.Get, "/pipeline/read", token);
    private static HttpRequestMessage PostWrite(string token) => Bearer(HttpMethod.Post, "/pipeline/write", token);
    private static HttpRequestMessage PostDeny(string token) => Bearer(HttpMethod.Post, "/pipeline/deny", token);

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task FullPat_AuthenticatesAsOwner_OnGet()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full);

        var response = await host.Client.SendAsync(Get(token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var probe = await response.Content.ReadFromJsonAsync<PipelineProbe>();
        Assert.NotNull(probe);
        Assert.Equal(host.User.Id, probe!.Uid);
        Assert.True(probe.IsPat);
        Assert.Equal("full", probe.Scope);
    }

    [Fact]
    public async Task ReadPat_AllowedOnGet_Forbidden403OnMutation()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Read);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get(token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(PostWrite(token))).StatusCode);
    }

    [Fact]
    public async Task FullPat_CanMutate()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(PostWrite(token))).StatusCode);
    }

    [Fact]
    public async Task ExpiredPat_Rejected401()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(Get(token))).StatusCode);
    }

    [Fact]
    public async Task RevokedPat_Rejected401_OnNextRequest()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(Get(token))).StatusCode);

        await host.RevokeBySecretAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(Get(token))).StatusCode);
    }

    [Fact]
    public async Task Pat_RefusedOnJwtOnlyEndpoint_EvenWithFullScope()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(PostDeny(token))).StatusCode);
    }

    [Fact]
    public async Task Jwt_StillAuthenticates_ThroughPolicyScheme()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var jwt = await host.IssueJwtAsync();

        var response = await host.Client.SendAsync(Get(jwt));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var probe = await response.Content.ReadFromJsonAsync<PipelineProbe>();
        Assert.False(probe!.IsPat);
        Assert.Equal(host.User.Id, probe.Uid);
    }

    [Fact]
    public async Task Pat_StampsLastUsed_OnUse()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        var token = await host.CreatePatAsync(PersonalAccessTokenScope.Full);
        Assert.Null(await host.GetLastUsedAsync(token));

        await host.Client.SendAsync(Get(token));

        Assert.NotNull(await host.GetLastUsedAsync(token));
    }

    [Fact]
    public async Task NoToken_Unauthorized401()
    {
        await using var host = await PatPipelineHost.CreateAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/pipeline/read")).StatusCode);
    }
}
