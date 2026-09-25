using System.Text.Json;
using System.Text.Json.Serialization;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;

namespace Stashboard.Tests.Contracts;

/// <summary>
/// Guards the JSON wire form of the "stringly-typed enum" fields the frontend depends on.
/// These enums serialize via <see cref="JsonStringEnumMemberNameAttribute"/> to lowercase
/// (except EmailProvider, whose member names already match), so the frontend's string-literal
/// unions keep matching. Uses the same converter Program.cs registers globally.
/// </summary>
public class EnumWireFormatTests
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(DashboardSortMode.Name, "name")]
    [InlineData(DashboardSortMode.Category, "category")]
    [InlineData(DashboardSortMode.Custom, "custom")]
    public void DashboardSortMode_SerializesLowercase(DashboardSortMode mode, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(mode, Options));

    [Theory]
    [InlineData(Theme.System, "system")]
    [InlineData(Theme.Light, "light")]
    [InlineData(Theme.Dark, "dark")]
    public void Theme_SerializesLowercase(Theme theme, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(theme, Options));

    [Theory]
    [InlineData(PersonalAccessTokenScope.Read, "read")]
    [InlineData(PersonalAccessTokenScope.Full, "full")]
    public void PersonalAccessTokenScope_SerializesLowercase(PersonalAccessTokenScope scope, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(scope, Options));

    [Theory]
    [InlineData(EmailProvider.Smtp, "Smtp")]
    [InlineData(EmailProvider.LogOnly, "LogOnly")]
    public void EmailProvider_SerializesMemberName(EmailProvider provider, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(provider, Options));

    [Theory]
    [InlineData("\"custom\"", DashboardSortMode.Custom)]
    [InlineData("\"dark\"", Theme.Dark)]
    public void ReadsLowercaseWireBack(string json, object expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize(json, expected.GetType(), Options));
}
