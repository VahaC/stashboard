namespace Stashboard.Core.Enums;

/// <summary>
/// V2.4 — explicit authentication strategy for the registry client. Lets
/// users point a watch at Harbor / Nexus / Gitea Packages (HTTP Basic
/// only — no Bearer round-trip) or AWS ECR (temporary username/password
/// derived from IAM via <c>GetAuthorizationToken</c>).
/// </summary>
public enum RegistryAuthType
{
    /// <summary>Default behaviour: try anonymous, then follow a Bearer
    /// challenge if the registry asks for one. Works for Docker Hub and
    /// GHCR out of the box.</summary>
    Auto = 0,

    /// <summary>HTTP Basic on every request. Skip the Bearer round-trip
    /// even if the server advertises one — required for Nexus and many
    /// Gitea Packages setups where Bearer isn't supported at all.</summary>
    Basic = 1,

    /// <summary>AWS ECR. Resolves credentials via the ECR
    /// <c>GetAuthorizationToken</c> API at check time and uses the
    /// resulting Basic token. Cached for ~12h via <c>IMemoryCache</c>.</summary>
    AwsEcr = 2,
}
