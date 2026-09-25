namespace Stashboard.Api.Contracts;

/// <summary>The VAPID application-server public key the browser needs to subscribe.</summary>
public sealed record VapidPublicKeyResponse(string PublicKey);

/// <summary>The subscription keys a browser's <c>PushSubscription.toJSON()</c> produces.</summary>
public sealed record PushSubscriptionKeys(string P256dh, string Auth);

/// <summary>A device subscription POSTed by the browser after <c>pushManager.subscribe</c>.</summary>
public sealed record PushSubscriptionRequest(string Endpoint, PushSubscriptionKeys Keys);

/// <summary>One of the user's subscribed devices, for the Settings device list. No key material.</summary>
public sealed record PushDeviceResponse(Guid Id, string? Label, DateTime CreatedUtc, DateTime? LastSuccessUtc);

/// <summary>Result of sending a test push to every one of the user's devices.</summary>
public sealed record PushTestResponse(int Attempted, int Delivered, int Pruned);
