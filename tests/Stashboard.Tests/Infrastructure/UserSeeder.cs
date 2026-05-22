using Stashboard.Api.Data;

namespace Stashboard.Tests.Infrastructure;

public sealed class UserSeeder(IDataFactory dataFactory) : IUserSeeder
{
    public UserEntity Owner { get; private set; } = default!;
    public UserEntity Other { get; private set; } = default!;

    public async Task SeedAsync()
    {
        Owner = await dataFactory.UserAsync("owner@test.local");
        Other = await dataFactory.UserAsync("other@test.local");
    }
}
