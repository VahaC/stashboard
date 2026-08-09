using System.Text;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V3.3 — covers <see cref="DockerLogStreamer.BuildLine"/>, the per-line
/// decoder that turns a stdcopy payload slice into a wire
/// <see cref="DockerLogLine"/>. The wider streaming loop is exercised by
/// the controller-level <c>LogsEndpointTests</c> via a mocked streamer —
/// here we cover the bits that are easy to get wrong on a unit-test bench
/// and very hard to reproduce in an integration test: trailing-CR strip,
/// nanosecond-precision timestamp parsing, and timezone-offset prefixes.
/// </summary>
public class DockerLogStreamerTests
{
    [Fact]
    public void BuildLine_StripsTrailingCarriageReturn()
    {
        var line = DockerLogStreamer.BuildLine(
            Encoding.UTF8.GetBytes("hello world\r"),
            DockerLogStreamChannel.Stdout,
            timestampsEnabled: false);

        Assert.Equal("hello world", line.Message);
        Assert.Null(line.TimestampUtc);
        Assert.Equal(DockerLogStreamChannel.Stdout, line.Stream);
    }

    [Fact]
    public void BuildLine_ParsesRfc3339NanoPrefix_WithFullNanosecondPrecision()
    {
        var line = DockerLogStreamer.BuildLine(
            Encoding.UTF8.GetBytes("2026-05-19T18:32:01.123456789Z hello"),
            DockerLogStreamChannel.Stdout,
            timestampsEnabled: true);

        Assert.NotNull(line.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, line.TimestampUtc!.Value.Kind);
        Assert.Equal(2026, line.TimestampUtc.Value.Year);
        Assert.Equal(5, line.TimestampUtc.Value.Month);
        Assert.Equal(19, line.TimestampUtc.Value.Day);
        Assert.Equal(18, line.TimestampUtc.Value.Hour);
        // We drop sub-100ns precision — the millisecond field must survive.
        Assert.Equal(123, line.TimestampUtc.Value.Millisecond);
        Assert.Equal("hello", line.Message);
    }

    [Fact]
    public void BuildLine_ParsesRfc3339NanoPrefix_WithTimezoneOffset()
    {
        var line = DockerLogStreamer.BuildLine(
            Encoding.UTF8.GetBytes("2026-05-19T20:32:01.500000000+02:00 hello"),
            DockerLogStreamChannel.Stdout,
            timestampsEnabled: true);

        Assert.NotNull(line.TimestampUtc);
        Assert.Equal(18, line.TimestampUtc!.Value.Hour);
        Assert.Equal(32, line.TimestampUtc.Value.Minute);
        Assert.Equal("hello", line.Message);
    }

    [Fact]
    public void BuildLine_LeavesMessageIntact_WhenTimestampsDisabled()
    {
        var line = DockerLogStreamer.BuildLine(
            Encoding.UTF8.GetBytes("2026-05-19T18:32:01Z hello"),
            DockerLogStreamChannel.Stderr,
            timestampsEnabled: false);

        Assert.Null(line.TimestampUtc);
        Assert.Equal("2026-05-19T18:32:01Z hello", line.Message);
        Assert.Equal(DockerLogStreamChannel.Stderr, line.Stream);
    }

    [Fact]
    public void BuildLine_DoesNotStripPrefix_WhenLeadingTokenIsNotATimestamp()
    {
        var line = DockerLogStreamer.BuildLine(
            Encoding.UTF8.GetBytes("INFO starting server"),
            DockerLogStreamChannel.Stdout,
            timestampsEnabled: true);

        Assert.Null(line.TimestampUtc);
        Assert.Equal("INFO starting server", line.Message);
    }

    [Fact]
    public void BuildLine_HandlesEmptyPayload()
    {
        var line = DockerLogStreamer.BuildLine(
            Array.Empty<byte>(),
            DockerLogStreamChannel.Stdout,
            timestampsEnabled: true);

        Assert.Equal(string.Empty, line.Message);
        Assert.Null(line.TimestampUtc);
    }
}



