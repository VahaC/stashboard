using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V3.4 — <c>GET /watches/{id}/stats</c> ("Live container stats").
/// Verifies the controller wires the right transport / container / request
/// onto <see cref="IDockerStatsStreamer"/>, serialises samples as NDJSON,
/// and surfaces the same error envelopes as the V3.3 logs endpoint.
/// </summary>
public class StatsEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Stats_Streams_NdjsonFrames_OnSuccess()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "wp");

        var samples = new[]
        {
            new DockerContainerStatsSample(
                TimestampUtc: new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
                CpuPercent: null,
                MemoryUsageBytes: 100UL * 1024 * 1024,
                MemoryLimitBytes: 512UL * 1024 * 1024,
                MemoryPercent: 19.5d,
                NetworkRxBytes: 1024,
                NetworkTxBytes: 512,
                BlockReadBytes: 0,
                BlockWriteBytes: 0,
                OnlineCpus: 4),
            new DockerContainerStatsSample(
                TimestampUtc: new DateTime(2026, 5, 19, 12, 0, 1, DateTimeKind.Utc),
                CpuPercent: 25.5d,
                MemoryUsageBytes: 110UL * 1024 * 1024,
                MemoryLimitBytes: 512UL * 1024 * 1024,
                MemoryPercent: 21.5d,
                NetworkRxBytes: 2048,
                NetworkTxBytes: 1024,
                BlockReadBytes: 4096,
                BlockWriteBytes: 0,
                OnlineCpus: 4),
        };

        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(),
                "wp",
                It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (DockerHostTransport _, string _, DockerStatsStreamRequest _,
                Func<DockerContainerStatsSample, CancellationToken, Task> onSample, CancellationToken ct) =>
            {
                foreach (var sample in samples) await onSample(sample, ct);
                return DockerStatsStreamResult.Ok;
            });

        var ctrl = BuildController();
        var (body, response) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamStats(svc.Id, watch.Id, oneShot: false, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("application/x-ndjson", response.ContentType);

        var text = Encoding.UTF8.GetString(body.ToArray());
        var frames = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, frames.Length);
        // First frame has no CPU baseline.
        Assert.Contains("\"cpuPercent\":null", frames[0]);
        Assert.Contains("\"memoryUsageBytes\":104857600", frames[0]);
        // Second frame carries the computed CPU%.
        Assert.Contains("\"cpuPercent\":25.5", frames[1]);
        Assert.Contains("\"onlineCpus\":4", frames[1]);
    }

    [Fact]
    public async Task Stats_PropagatesOneShotFlag_ToStreamer()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "api");

        DockerStatsStreamRequest? captured = null;
        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(),
                "api",
                It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((DockerHostTransport _, string _, DockerStatsStreamRequest req,
                Func<DockerContainerStatsSample, CancellationToken, Task> _, CancellationToken _) =>
            {
                captured = req;
                return Task.FromResult(DockerStatsStreamResult.Ok);
            });

        var ctrl = BuildController();
        AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamStats(svc.Id, watch.Id, oneShot: true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.NotNull(captured);
        Assert.False(captured!.Stream);
        Assert.True(captured.OneShot);
    }

    [Fact]
    public async Task Stats_DefaultsToStreaming_WhenOneShotMissing()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "api");

        DockerStatsStreamRequest? captured = null;
        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(), "api", It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((DockerHostTransport _, string _, DockerStatsStreamRequest req,
                Func<DockerContainerStatsSample, CancellationToken, Task> _, CancellationToken _) =>
            {
                captured = req;
                return Task.FromResult(DockerStatsStreamResult.Ok);
            });

        var ctrl = BuildController();
        AttachResponseBuffer(ctrl);

        await ctrl.StreamStats(svc.Id, watch.Id, oneShot: false, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.Stream);
        Assert.False(captured.OneShot);
    }

    [Fact]
    public async Task Stats_Returns404_WhenWatchBelongsToAnotherUser()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var watch = await SeedWatchAsync(svc.Id, _otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.StreamStats(svc.Id, watch.Id, oneShot: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _statsStreamerMock.Verify(s => s.StreamStatsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerStatsStreamRequest>(),
            It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stats_Returns404_WhenWatchDoesNotExist()
    {
        var svc = await _dataFactory.ServiceAsync();

        var ctrl = BuildController();
        var result = await ctrl.StreamStats(svc.Id, Guid.NewGuid(), oneShot: false, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Stats_WritesErrorFrame_WhenStreamerReturnsContainerNotFound()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "ghost");

        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(), "ghost", It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerStatsStreamResult(
                DockerHostStatus.ContainerNotFound,
                "Container 'ghost' not found on the configured Docker host."));

        var ctrl = BuildController();
        var (body, _) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamStats(svc.Id, watch.Id, oneShot: false, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"stream\":\"error\"", text);
        Assert.Contains("ghost", text);
    }

    [Fact]
    public async Task Stats_WritesErrorFrame_WhenHostThrows()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);

        _statsStreamerMock
            .Setup(s => s.StreamStatsAsync(
                It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerStatsStreamRequest>(),
                It.IsAny<Func<DockerContainerStatsSample, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connect refused"));

        var ctrl = BuildController();
        var (body, _) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamStats(svc.Id, watch.Id, oneShot: false, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"stream\":\"error\"", text);
        Assert.Contains("connect refused", text);
    }

    /// <summary>
    /// The controller writes the NDJSON frames directly into
    /// <c>HttpResponse.Body</c>, so the test buffers the response in a
    /// <see cref="MemoryStream"/> and disables the buffer-size guard the
    /// default <c>HttpResponse</c> imposes (zero-length content length).
    /// </summary>
    private static (MemoryStream Body, HttpResponse Response) AttachResponseBuffer(
        Api.Controllers.DockerWatchesController ctrl)
    {
        var body = new MemoryStream();
        var httpContext = ctrl.ControllerContext.HttpContext;
        httpContext.Response.Body = body;
        httpContext.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return (body, httpContext.Response);
    }
}
