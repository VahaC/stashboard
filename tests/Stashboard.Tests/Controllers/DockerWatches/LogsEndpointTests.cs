using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V3.3 — <c>GET /watches/{id}/logs</c> ("Real-time container logs").
/// Verifies the controller wires the right transport / container / request
/// onto <see cref="IDockerLogStreamer"/>, serialises log frames as NDJSON,
/// and surfaces the same error envelopes as the V3.1 inspect endpoint.
/// </summary>
public class LogsEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Logs_Streams_NdjsonFrames_OnSuccess()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "wp");

        var emitted = new List<DockerLogLine>
        {
            new(DockerLogStreamChannel.Stdout, new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc), "starting"),
            new(DockerLogStreamChannel.Stderr, null, "warning: stub"),
        };

        _logStreamerMock
            .Setup(s => s.StreamLogsAsync(
                It.IsAny<DockerHostTransport>(),
                "wp",
                It.IsAny<DockerLogStreamRequest>(),
                It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (DockerHostTransport _, string _, DockerLogStreamRequest _,
                Func<DockerLogLine, CancellationToken, Task> onLine, CancellationToken ct) =>
            {
                foreach (var line in emitted) await onLine(line, ct);
                return DockerLogStreamResult.Ok;
            });

        var ctrl = BuildController();
        var (body, response) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamLogs(
            svc.Id, watch.Id, follow: false, tail: 200, since: null,
            timestamps: true, stdout: true, stderr: true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal("application/x-ndjson", response.ContentType);

        var text = Encoding.UTF8.GetString(body.ToArray());
        var frames = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, frames.Length);
        Assert.Contains("\"stream\":\"stdout\"", frames[0]);
        Assert.Contains("\"message\":\"starting\"", frames[0]);
        Assert.Contains("\"stream\":\"stderr\"", frames[1]);
        Assert.Contains("\"message\":\"warning: stub\"", frames[1]);
    }

    [Fact]
    public async Task Logs_PropagatesQueryParams_ToStreamer()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId, containerName: "api");

        DockerLogStreamRequest? captured = null;
        _logStreamerMock
            .Setup(s => s.StreamLogsAsync(
                It.IsAny<DockerHostTransport>(),
                "api",
                It.IsAny<DockerLogStreamRequest>(),
                It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((DockerHostTransport _, string _, DockerLogStreamRequest req,
                Func<DockerLogLine, CancellationToken, Task> _, CancellationToken _) =>
            {
                captured = req;
                return Task.FromResult(DockerLogStreamResult.Ok);
            });

        var ctrl = BuildController();
        AttachResponseBuffer(ctrl);

        var since = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var result = await ctrl.StreamLogs(
            svc.Id, watch.Id, follow: true, tail: 50, since: since,
            timestamps: false, stdout: false, stderr: true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.NotNull(captured);
        Assert.True(captured!.Follow);
        Assert.Equal(50, captured.Tail);
        Assert.NotNull(captured.Since);
        Assert.Equal(since, captured.Since!.Value.ToUnixTimeSeconds());
        Assert.False(captured.Timestamps);
        Assert.False(captured.IncludeStdout);
        Assert.True(captured.IncludeStderr);
    }

    [Fact]
    public async Task Logs_Returns400_WhenBothStreamsDisabled()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);

        var ctrl = BuildController();
        var result = await ctrl.StreamLogs(
            svc.Id, watch.Id, follow: false, tail: 200, since: null,
            timestamps: true, stdout: false, stderr: false, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
        _logStreamerMock.Verify(s => s.StreamLogsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerLogStreamRequest>(),
            It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Logs_Returns404_WhenWatchBelongsToAnotherUser()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var watch = await SeedWatchAsync(svc.Id, _otherUserId);

        var ctrl = BuildController();
        var result = await ctrl.StreamLogs(
            svc.Id, watch.Id, follow: false, tail: 200, since: null,
            timestamps: true, stdout: true, stderr: true, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _logStreamerMock.Verify(s => s.StreamLogsAsync(
            It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerLogStreamRequest>(),
            It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Logs_WritesErrorFrame_WhenStreamerReturnsHostUnreachable()
    {
        var svc = await _dataFactory.ServiceAsync();
        var watch = await SeedWatchAsync(svc.Id, _userId);

        _logStreamerMock
            .Setup(s => s.StreamLogsAsync(
                It.IsAny<DockerHostTransport>(), It.IsAny<string>(), It.IsAny<DockerLogStreamRequest>(),
                It.IsAny<Func<DockerLogLine, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerLogStreamResult(DockerHostStatus.HostUnreachable, "dns fail"));

        var ctrl = BuildController();
        var (body, _) = AttachResponseBuffer(ctrl);

        var result = await ctrl.StreamLogs(
            svc.Id, watch.Id, follow: false, tail: 200, since: null,
            timestamps: true, stdout: true, stderr: true, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"stream\":\"error\"", text);
        Assert.Contains("dns fail", text);
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
        // The controller flushes the response body — make sure the default
        // BodyControlFeature doesn't reject the writes because we never
        // explicitly call StartAsync.
        httpContext.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return (body, httpContext.Response);
    }
}
