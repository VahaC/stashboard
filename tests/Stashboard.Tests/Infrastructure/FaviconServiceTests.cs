using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Infrastructure.Services;

namespace Stashboard.Tests.Infrastructure;

public class FaviconServiceTests
{
    private static HttpClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responseFactory(req));
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task ResolveFaviconUrlAsync_UsesDirectFavicon_WhenImageExists()
    {
        var factory = new Mock<IHttpClientFactory>();
        var client = BuildClient(req =>
            req.RequestUri!.ToString().EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/x-icon") }
                    }
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        factory.Setup(f => f.CreateClient("favicon")).Returns(client);
        factory.Setup(f => f.CreateClient("favicon-insecure")).Returns(client);
        var sut = new FaviconService(factory.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<FaviconService>.Instance);

        var result = await sut.ResolveFaviconUrlAsync("https://example.com/app");

        Assert.Equal("https://example.com/favicon.ico", result);
    }

    [Fact]
    public async Task ResolveFaviconUrlAsync_FallsBackToHtmlLinkIcon_WhenDirectFaviconMissing()
    {
        var factory = new Mock<IHttpClientFactory>();
        var client = BuildClient(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head><link rel=\"icon\" href=\"/assets/site-icon.png\"></head></html>")
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") }
                }
            };
        });
        factory.Setup(f => f.CreateClient("favicon")).Returns(client);
        factory.Setup(f => f.CreateClient("favicon-insecure")).Returns(client);
        var sut = new FaviconService(factory.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<FaviconService>.Instance);

        var result = await sut.ResolveFaviconUrlAsync("https://example.com/app");

        Assert.Equal("https://example.com/assets/site-icon.png", result);
    }

    [Fact]
    public async Task InvalidateSiteFaviconCache_ForcesFreshResolution()
    {
        var requestCount = 0;
        var factory = new Mock<IHttpClientFactory>();
        var client = BuildClient(request =>
        {
            requestCount++;
            return request.RequestUri!.ToString().EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/x-icon") }
                    }
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        factory.Setup(f => f.CreateClient("favicon")).Returns(client);
        factory.Setup(f => f.CreateClient("favicon-insecure")).Returns(client);
        var sut = new FaviconService(factory.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<FaviconService>.Instance);

        await sut.ResolveFaviconUrlAsync("https://example.com/app");
        await sut.ResolveFaviconUrlAsync("https://example.com/app");
        sut.InvalidateSiteFaviconCache("https://example.com/app");
        await sut.ResolveFaviconUrlAsync("https://example.com/app");

        Assert.Equal(2, requestCount);
    }
}



