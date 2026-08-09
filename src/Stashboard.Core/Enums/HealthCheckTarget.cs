namespace Stashboard.Core.Enums;

/// <summary>
/// V10.1 — which of a service's two probed URLs a <see cref="Entities.HealthCheckEventEntity"/>
/// row belongs to. A service has a canonical <c>Main</c> URL (the one that drives the card
/// dot + offline alerts) and an optional <c>Additional</c> URL; uptime history is kept per
/// target so each gets its own uptime %, response-time trend and incident log.
/// </summary>
public enum HealthCheckTarget
{
    Main = 0,
    Additional = 1,
}
