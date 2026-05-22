using Riok.Mapperly.Abstractions;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Mapping;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public sealed partial class StashboardMapper : IStashboardMapper
{
    public partial UserResponse MapToUserResponse(UserEntity entity);

    public partial ProfileResponse MapToProfileResponse(UserEntity entity);

    public DashboardPreferencesResponse MapToDashboardPreferencesResponse(UserEntity entity) =>
        new(entity.DashboardSortMode, entity.DashboardGroupByCategory);

    public TelegramSettingsResponse MapToTelegramSettingsResponse(UserEntity entity) =>
        new(entity.TelegramBotToken, entity.TelegramChatId, entity.TelegramNotificationsEnabled);

    public EmailSettingsResponse MapToEmailSettingsResponse(EmailSettingsEntity entity) =>
        new(entity.Provider, entity.Host, entity.Port, entity.UseStartTls, entity.Username,
            !string.IsNullOrEmpty(entity.PasswordEncrypted), entity.FromAddress, entity.FromName, entity.AppBaseUrl);

    // ServiceCount is a computed value (0 by default, composed by the caller with `with`).
    public CategoryResponse MapToCategoryResponse(CategoryEntity entity) =>
        new(entity.Id, entity.Name, entity.Color, 0);

    public TagResponse MapToTagResponse(TagEntity entity) =>
        new(entity.Id, entity.Name, 0);
}
