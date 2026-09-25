using System.Text.Json.Serialization;

namespace Stashboard.Api.Contracts;

/// <summary>
/// How the dashboard orders service cards. Serialized to/from its lowercase wire form
/// (<c>name</c> / <c>category</c> / <c>custom</c>) by the global string-enum converter,
/// so an out-of-range value is rejected by model binding instead of a hand-rolled regex.
/// </summary>
public enum DashboardSortMode
{
    [JsonStringEnumMemberName("name")] Name,
    [JsonStringEnumMemberName("category")] Category,
    [JsonStringEnumMemberName("custom")] Custom,
}
