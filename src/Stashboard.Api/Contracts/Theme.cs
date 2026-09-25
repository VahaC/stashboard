using System.Text.Json.Serialization;

namespace Stashboard.Api.Contracts;

/// <summary>
/// UI colour theme preference. Serialized to/from its lowercase wire form
/// (<c>system</c> / <c>light</c> / <c>dark</c>) by the global string-enum converter,
/// so an out-of-range value is rejected by model binding — no regex needed.
/// </summary>
public enum Theme
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}
