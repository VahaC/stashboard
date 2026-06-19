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

    /// <summary>
    /// V7.9 — same as <see cref="MapAsync(WebResourceEntity, CancellationToken)"/>
    /// but also resolves the service's Proxmox guest links into the update badge
    /// and the linked-guest summary list, using a caller-supplied lookup of the
    /// user's guests keyed by their stable <c>(connection, vmid)</c> key. The
    /// mapper stays db-free; the controller owns the (single, batched) guest query.
    /// A link whose guest row is missing from the lookup (e.g. destroyed) is
    /// silently dropped from both the badge and the summary.
    /// </summary>
    Task<WebResourceResponse> MapAsync(
        WebResourceEntity entity,
        IReadOnlyDictionary<(Guid ProxmoxConnectionId, int VmId), ProxmoxGuestEntity>? proxmoxGuests,
        CancellationToken cancellationToken);
}
