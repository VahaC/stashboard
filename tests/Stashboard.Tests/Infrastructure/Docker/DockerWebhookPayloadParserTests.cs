using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.6 — covers Docker Hub <c>push</c> events, GHCR <c>registry_package</c>
/// events, generic OCI distribution pushes, and all the unhappy cases the
/// parser must tolerate (the URL token already authenticated the call so a
/// bad body is never a 4xx).
/// </summary>
public class DockerWebhookPayloadParserTests
{
    private readonly DockerWebhookPayloadParser _sut = new();

    [Fact]
    public void Parse_DockerHubPushEvent_ExtractsRepoTagAndPushedAt()
    {
        const string body = """
        {
          "push_data": { "tag": "latest", "pushed_at": 1715900400 },
          "repository": { "repo_name": "linuxserver/sonarr" }
        }
        """;

        var payload = _sut.Parse(body);

        Assert.Equal("docker-hub", payload.Source);
        Assert.Equal("linuxserver/sonarr", payload.Repository);
        Assert.Equal("latest", payload.Tag);
        Assert.Equal(new DateTime(2024, 5, 16, 23, 0, 0, DateTimeKind.Utc), payload.PushedAtUtc);
    }

    [Fact]
    public void Parse_GhcrRegistryPackageEvent_ExtractsRepoAndTag()
    {
        const string body = """
        {
          "action": "published",
          "registry_package": {
            "name": "stashboard",
            "namespace": "vahac",
            "updated_at": "2026-05-18T08:15:30Z",
            "package_version": {
              "container_metadata": { "tag": { "name": "v2.6.0" } }
            }
          }
        }
        """;

        var payload = _sut.Parse(body);

        Assert.Equal("ghcr", payload.Source);
        Assert.Equal("vahac/stashboard", payload.Repository);
        Assert.Equal("v2.6.0", payload.Tag);
        Assert.Equal(new DateTime(2026, 5, 18, 8, 15, 30, DateTimeKind.Utc), payload.PushedAtUtc);
    }

    [Fact]
    public void Parse_GenericOciPushEvent_PicksTheFirstPushAction()
    {
        const string body = """
        {
          "events": [
            { "action": "pull",  "target": { "repository": "x/y", "tag": "old" }, "timestamp": "2026-05-18T07:00:00Z" },
            { "action": "push",  "target": { "repository": "stash/app", "tag": "v1" }, "timestamp": "2026-05-18T08:00:00Z" }
          ]
        }
        """;

        var payload = _sut.Parse(body);

        Assert.Equal("generic-oci", payload.Source);
        Assert.Equal("stash/app", payload.Repository);
        Assert.Equal("v1", payload.Tag);
        Assert.Equal(new DateTime(2026, 5, 18, 8, 0, 0, DateTimeKind.Utc), payload.PushedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]                          // root not an object
    [InlineData("{ \"completely\": \"unrelated\" }")]  // recognised shape missing
    public void Parse_UnknownOrMalformedBody_ReturnsUnknownInsteadOfThrowing(string? body)
    {
        var payload = _sut.Parse(body);

        Assert.Equal(DockerWebhookPayload.Unknown, payload);
    }

    [Fact]
    public void Parse_DockerHubBody_MissingPushedAt_StillRecognisedAsDockerHub()
    {
        const string body = """
        { "push_data": { "tag": "1.27" }, "repository": { "repo_name": "library/nginx" } }
        """;

        var payload = _sut.Parse(body);

        Assert.Equal("docker-hub", payload.Source);
        Assert.Equal("library/nginx", payload.Repository);
        Assert.Equal("1.27", payload.Tag);
        Assert.Null(payload.PushedAtUtc);
    }
}
