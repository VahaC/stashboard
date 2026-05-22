using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Creates and persists test entities directly in the database.
/// All methods save immediately and clear the EF change tracker.
/// </summary>
public interface IDataFactory
{
    Task<UserEntity> UserAsync(string? email = null, string password = "P@ssword1");

    Task<WebResourceEntity> ServiceAsync(
        Guid? userId = null,
        string name = "My Service",
        string mainUrl = "https://example.com",
        string? notes = null,
        Guid? categoryId = null);

    Task<CategoryEntity> CategoryAsync(
        Guid? userId = null,
        string name = "Infrastructure",
        string color = "#ff0000");

    Task<TagEntity> TagAsync(
        string name = "tag",
        Guid? userId = null);

    Task AttachTagAsync(WebResourceEntity service, TagEntity tag);

    Task AttachCredentialAsync(
        WebResourceEntity service,
        string key,
        string value,
        bool isSecret = true);
}
