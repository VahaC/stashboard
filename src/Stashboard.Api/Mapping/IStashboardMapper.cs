using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Mapping;

/// <summary>
/// Compile-time mapper (Mapperly) for all simple Entity → Dto/Response mappings in the API.
/// Per <c>REQUIREMENTS.md</c> rule #6 entities must never be mapped to DTOs inline.
/// Computed values (such as <c>ServiceCount</c>) default to <c>0</c> here and are composed
/// by the caller using a record <c>with</c> expression.
/// </summary>
public interface IStashboardMapper
{
    UserResponse MapToUserResponse(UserEntity entity);
    ProfileResponse MapToProfileResponse(UserEntity entity);
    DashboardPreferencesResponse MapToDashboardPreferencesResponse(UserEntity entity);
    TelegramSettingsResponse MapToTelegramSettingsResponse(UserEntity entity);
    EmailSettingsResponse MapToEmailSettingsResponse(EmailSettingsEntity entity);
    CategoryResponse MapToCategoryResponse(CategoryEntity entity);
    TagResponse MapToTagResponse(TagEntity entity);
}
