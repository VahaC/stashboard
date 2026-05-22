using Stashboard.Api.Mapping;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Creates a real <see cref="IStashboardMapper"/> configured with the production
/// <see cref="StashboardMapper"/> for use in tests. Test code must never hand-roll
/// Entity → Dto mapping (REQUIREMENTS.md rule #6).
/// </summary>
public static class TestMapperFactory
{
    public static IStashboardMapper Create() => new StashboardMapper();
}
