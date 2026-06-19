using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stashboard.Api.Serialization;

/// <summary>
/// Serializes every <see cref="DateTime"/> as a UTC ISO-8601 instant with a
/// trailing <c>Z</c>. The whole backend stores UTC (fields are named <c>*Utc</c>),
/// but EF Core's SQLite provider reads <see cref="DateTime"/> values back with
/// <see cref="DateTimeKind.Unspecified"/>, which System.Text.Json would otherwise
/// emit without a zone marker — making the browser's <c>new Date(...)</c> parse
/// them as <em>local</em> time and shift them by the client's UTC offset. (That
/// drift is what made a 1-hour maintenance snooze read as already expired.)
/// Read is left at the default so request parsing is unchanged.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc);
    }
}
