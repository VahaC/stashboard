using Riok.Mapperly.Abstractions;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Mapping;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public sealed partial class StashboardMapper : IStashboardMapper
{
    public partial UserResponse MapToUserResponse(UserEntity entity);

    // Hand-written (not Mapperly-generated) because OidcLinked is computed from OidcSubject
    // rather than a direct property copy — mirrors MapToEmailSettingsResponse's presence flag.
    public ProfileResponse MapToProfileResponse(UserEntity entity) =>
        new(entity.Id, entity.Email, entity.DisplayName, entity.EmailConfirmed, entity.PendingEmail,
            entity.Theme, entity.CreatedUtc, entity.LastLoginUtc, entity.TwoFactorEnabled,
            !string.IsNullOrEmpty(entity.OidcSubject), entity.LocalLoginDisabled);

    public DashboardPreferencesResponse MapToDashboardPreferencesResponse(UserEntity entity) =>
        new(entity.DashboardSortMode, entity.DashboardGroupByCategory);

    public TelegramSettingsResponse MapToTelegramSettingsResponse(UserEntity entity) =>
        new(entity.TelegramBotToken, entity.TelegramChatId, entity.TelegramNotificationsEnabled);

    public EmailSettingsResponse MapToEmailSettingsResponse(EmailSettingsEntity entity) =>
        new(entity.Provider, entity.Host, entity.Port, entity.UseStartTls, entity.Username,
            !string.IsNullOrEmpty(entity.PasswordEncrypted), entity.FromAddress, entity.FromName, entity.AppBaseUrl);

    // ServiceCount is a computed value (0 by default, composed by the caller with `with`).
    public CategoryResponse MapToCategoryResponse(CategoryEntity entity) =>
        new(entity.Id, entity.Name, entity.Color, 0, entity.SortOrder);

    public TagResponse MapToTagResponse(TagEntity entity) =>
        new(entity.Id, entity.Name, 0);
}
