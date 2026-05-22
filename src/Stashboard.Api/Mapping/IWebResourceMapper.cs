using Stashboard.Api.Contracts;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Mapping;

/// <summary>
/// Custom mapper for <see cref="WebResourceEntity"/> that requires runtime services
/// (favicon resolution + credential decryption) which AutoMapper would otherwise
/// have to consume through value resolvers. Implemented as a dedicated mapper service
/// (Adapter pattern) per <c>REQUIREMENTS.md</c> rule #6.
/// </summary>
public interface IWebResourceMapper
{
    Task<WebResourceResponse> MapAsync(WebResourceEntity entity, CancellationToken cancellationToken);
}
