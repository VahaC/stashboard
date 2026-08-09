using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Infrastructure.Docker;
using Stashboard.Infrastructure.Services;

namespace Stashboard.Tests.Infrastructure;

public class ContainerIconResolverTests
{
    private static HttpClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory, Action? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                onRequest?.Invoke();
                return responseFactory(req);
            });
        return new HttpClient(handler.Object);
    }

    private static ContainerIconResolver BuildResolver(HttpClient client, IMemoryCache? cache = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ContainerIconResolver.HttpClientName)).Returns(client);
        return new ContainerIconResolver(
            factory.Object,
            new ImageReferenceParser(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ContainerIconResolver>.Instance);
    }

    private static HttpResponseMessage WebpHit(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp") }
            }
        };

    // ── SlugFor: derivation across registries ────────────────────────────────

    [Theory]
    [InlineData("nginx", "nginx")]                                       // docker.io library shorthand
    [InlineData("library/postgres", "postgresql")]                      // docker.io + alias
    [InlineData("postgres:16", "postgresql")]                           // alias, tag stripped
    [InlineData("lscr.io/linuxserver/jellyfin:latest", "jellyfin")]     // lscr.io, last segment
    [InlineData("ghcr.io/home-assistant/home-assistant:stable", "home-assistant")] // ghcr.io
    [InlineData("registry.example.com:5000/team/app:1.2.3", "app")]     // private registry
    [InlineData("arm64v8/nginx", "nginx")]                              // arch prefix dropped
    public void SlugFor_DerivesExpectedSlug(string imageReference, string expected)
    {
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Equal(expected, resolver.SlugFor(imageReference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@not a ref@@@")]
    public void SlugFor_ReturnsNull_ForUnparseableInput(string imageReference)
    {
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(resolver.SlugFor(imageReference));
    }

    // ── SlugForOs: Proxmox guest ostype → dashboard-icons slug ───────────────

    [Theory]
    [InlineData("debian", "debian")]
    [InlineData("ubuntu", "ubuntu")]
    [InlineData("alpine", "alpine-linux")]
    [InlineData("archlinux", "arch-linux")]
    [InlineData("rockylinux", "rocky-linux")]
    [InlineData("l26", "linux")]            // generic Linux VM
    [InlineData("win10", "windows")]        // any Windows VM variant
    [InlineData("w2k19", "windows")]
    public void SlugForOs_MapsExpectedSlug(string osType, string expected)
    {
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal(expected, resolver.SlugForOs(osType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("haiku-os")]
    public void SlugForOs_ReturnsNull_ForUnknownOrEmpty(string? osType)
    {
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(resolver.SlugForOs(osType));
    }

    [Fact]
    public async Task ResolveIconForOsAsync_FetchesOfficialOsIcon()
    {
        var resolver = BuildResolver(BuildClient(req =>
            req.RequestUri!.AbsoluteUri.EndsWith("/webp/debian.webp", StringComparison.OrdinalIgnoreCase)
                ? WebpHit([4, 2])
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await resolver.ResolveIconForOsAsync("debian");

        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String([4, 2])}", result);
    }

    // ── ResolveIconDataUriAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ResolveIconDataUriAsync_ReturnsDataUri_OnHit()
    {
        var resolver = BuildResolver(BuildClient(req =>
            req.RequestUri!.AbsoluteUri.EndsWith("/webp/jellyfin.webp", StringComparison.OrdinalIgnoreCase)
                ? WebpHit([1, 2, 3])
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await resolver.ResolveIconDataUriAsync("jellyfin");

        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String([1, 2, 3])}", result);
    }

    [Fact]
    public async Task ResolveIconDataUriAsync_ReturnsNull_On404()
    {
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await resolver.ResolveIconDataUriAsync("does-not-exist"));
    }

    [Fact]
    public async Task ResolveIconDataUriAsync_CachesHit_SecondCallSkipsNetwork()
    {
        var requestCount = 0;
        var resolver = BuildResolver(BuildClient(_ => WebpHit([9]), onRequest: () => requestCount++));

        var first = await resolver.ResolveIconDataUriAsync("sonarr");
        var second = await resolver.ResolveIconDataUriAsync("sonarr");

        Assert.Equal(first, second);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ResolveIconDataUriAsync_CachesMiss_SecondCallSkipsNetwork()
    {
        var requestCount = 0;
        var resolver = BuildResolver(BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound), onRequest: () => requestCount++));

        var first = await resolver.ResolveIconDataUriAsync("nope");
        var second = await resolver.ResolveIconDataUriAsync("nope");

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, requestCount);
    }

    // ── ResolveIconForImageAsync (slug + fetch in one) ───────────────────────

    [Fact]
    public async Task ResolveIconForImageAsync_FetchesIconForDerivedSlug()
    {
        var resolver = BuildResolver(BuildClient(req =>
            req.RequestUri!.AbsoluteUri.EndsWith("/webp/jellyfin.webp", StringComparison.OrdinalIgnoreCase)
                ? WebpHit([7, 7, 7])
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await resolver.ResolveIconForImageAsync("lscr.io/linuxserver/jellyfin:latest");

        Assert.Equal($"data:image/webp;base64,{Convert.ToBase64String([7, 7, 7])}", result);
    }

    [Fact]
    public async Task ResolveIconForImageAsync_ReturnsNull_WhenSlugUnderivable()
    {
        var requestCount = 0;
        var resolver = BuildResolver(BuildClient(_ => WebpHit([1]), onRequest: () => requestCount++));

        var result = await resolver.ResolveIconForImageAsync("   ");

        Assert.Null(result);
        Assert.Equal(0, requestCount); // never touches the network
    }
}



