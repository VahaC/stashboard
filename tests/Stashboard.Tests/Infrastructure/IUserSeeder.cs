using Stashboard.Api.Data;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Seeds users into the database for use in tests.
/// Users are created via <see cref="IDataFactory"/> and their IDs exposed as properties.
/// </summary>
public interface IUserSeeder
{
    UserEntity Owner { get; }
    UserEntity Other { get; }

    Task SeedAsync();
}



