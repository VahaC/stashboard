namespace Stashboard.Api.Services.Mqtt;

/// <summary>
/// V9.0 — yields the current set of source signals the publisher should surface:
/// Docker container running-state + image-update, Proxmox guest running-state, and
/// service health. Implemented over the live Docker daemons + the DB; faked in
/// tests so the publisher's reconcile / lifecycle logic is exercised without I/O.
/// </summary>
public interface IMqttEntityStateProvider
{
    Task<IReadOnlyList<MqttSourceEntity>> GetEntitiesAsync(CancellationToken cancellationToken = default);
}
