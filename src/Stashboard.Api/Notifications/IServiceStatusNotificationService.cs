using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Notifications;

public interface IServiceStatusNotificationService
{
    Task NotifyIfNeededAsync(
        UserEntity user,
        WebResourceEntity service,
        ServiceStatus previousMainStatus,
        ServiceStatus previousAdditionalStatus,
        CancellationToken cancellationToken = default);
}
