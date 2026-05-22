using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Mapping;

public class StashboardMapperTests
{
    [Fact]
    public void UserEntity_To_UserResponse_MapsScalarFields()
    {
        var mapper = TestMapperFactory.Create();
        var user = new UserEntity { Email = "u@x.com" };

        var dto = mapper.MapToUserResponse(user);

        Assert.Equal(user.Id, dto.Id);
        Assert.Equal("u@x.com", dto.Email);
    }

    [Fact]
    public void UserEntity_To_ProfileResponse_MapsAllFields()
    {
        var mapper = TestMapperFactory.Create();
        var user = new UserEntity
        {
            Email = "u@x.com",
            DisplayName = "Alice",
            EmailConfirmed = true,
            PendingEmail = "new@x.com",
            LastLoginUtc = DateTime.UtcNow,
        };

        var dto = mapper.MapToProfileResponse(user);

        Assert.Equal(user.Id, dto.Id);
        Assert.Equal(user.Email, dto.Email);
        Assert.Equal(user.DisplayName, dto.DisplayName);
        Assert.True(dto.EmailConfirmed);
        Assert.Equal(user.PendingEmail, dto.PendingEmail);
        Assert.Equal(user.CreatedUtc, dto.CreatedUtc);
        Assert.Equal(user.LastLoginUtc, dto.LastLoginUtc);
    }

    [Fact]
    public void UserEntity_To_DashboardPreferencesResponse_MapsFields()
    {
        var mapper = TestMapperFactory.Create();
        var user = new UserEntity
        {
            DashboardSortMode = "category",
            DashboardGroupByCategory = true,
        };

        var response = mapper.MapToDashboardPreferencesResponse(user);

        Assert.Equal("category", response.SortMode);
        Assert.True(response.GroupByCategory);
    }

    [Fact]
    public void CategoryEntity_To_CategoryResponse_DefaultsServiceCountToZero()
    {
        var mapper = TestMapperFactory.Create();
        var entity = new CategoryEntity { UserId = Guid.NewGuid(), Name = "Web", Color = "#fff" };

        var dto = mapper.MapToCategoryResponse(entity);

        Assert.Equal(entity.Id, dto.Id);
        Assert.Equal("Web", dto.Name);
        Assert.Equal("#fff", dto.Color);
        Assert.Equal(0, dto.ServiceCount);
    }

    [Fact]
    public void CategoryResponse_WithExpression_ReplacesServiceCount()
    {
        var mapper = TestMapperFactory.Create();
        var entity = new CategoryEntity { Name = "Web", Color = "#000" };

        var dto = mapper.MapToCategoryResponse(entity) with { ServiceCount = 7 };

        Assert.Equal(7, dto.ServiceCount);
        Assert.Equal("Web", dto.Name);
    }

    [Fact]
    public void TagEntity_To_TagResponse_DefaultsServiceCountToZero()
    {
        var mapper = TestMapperFactory.Create();
        var entity = new TagEntity { UserId = Guid.NewGuid(), Name = "api" };

        var dto = mapper.MapToTagResponse(entity);

        Assert.Equal(entity.Id, dto.Id);
        Assert.Equal("api", dto.Name);
        Assert.Equal(0, dto.ServiceCount);
    }
}
