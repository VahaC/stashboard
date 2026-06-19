using System.Text.Json;
using Stashboard.Api.Serialization;
using Xunit;

namespace Stashboard.Tests.Serialization;

public sealed class UtcDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new UtcDateTimeConverter() },
    };

    // Regression: EF Core's SQLite provider hands back DateTimes with Kind=Unspecified.
    // Without the converter those serialized without a 'Z', so the browser parsed them
    // as local time and a 1-hour snooze read as already expired. The converter must
    // mark them UTC on the wire.
    [Fact]
    public void Serialize_UnspecifiedKind_EmitsTrailingZ()
    {
        var value = new DateTime(2026, 6, 17, 22, 38, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options);

        Assert.EndsWith("Z\"", json);
        Assert.Contains("2026-06-17T22:38:00", json);
    }

    [Fact]
    public void Serialize_UtcKind_EmitsTrailingZ()
    {
        var value = new DateTime(2026, 6, 17, 22, 38, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(value, Options);

        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesInstant()
    {
        var value = new DateTime(2026, 6, 17, 22, 38, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(value, Options);
        var parsed = JsonSerializer.Deserialize<DateTime>(json, Options).ToUniversalTime();

        Assert.Equal(new DateTime(2026, 6, 17, 22, 38, 0, DateTimeKind.Utc), parsed);
    }
}
