using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stashboard.Api.Serialization;

/// <summary>
/// Shared options for the hand-written NDJSON streams (container logs / stats)
/// that serialize straight to <c>Response.Body</c> and so bypass the converters
/// configured on <c>AddControllers().AddJsonOptions(...)</c>. Mirrors that config
/// — camelCase, enums-as-strings, and UTC <see cref="DateTime"/>s with a trailing
/// <c>Z</c> (see <see cref="UtcDateTimeConverter"/>) — so a streamed
/// <c>timestamp</c> renders in the browser's local zone instead of being
/// mis-parsed as local time.
/// </summary>
public static class StreamingJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(),
            new UtcDateTimeConverter(),
        },
    };
}
