namespace Stashboard.Core.Enums;

/// <summary>
/// V2.7 — outcome of a one-click "Update now" attempt logged on
/// <see cref="Stashboard.Core.Entities.DockerUpdateAttemptEntity"/>. Every
/// attempt — successful or not — gets a row so the user can audit what
/// happened to their container.
/// </summary>
public enum DockerUpdateAttemptStatus
{
    /// <summary>The whole sequence (pull + recreate) completed and the
    /// container is running on the new image.</summary>
    Success = 0,

    /// <summary>Pulling the new image failed (network / auth / not-found).
    /// The original container is untouched.</summary>
    PullFailed = 1,

    /// <summary>The pull succeeded but stop / remove / create / start
    /// failed. Recovery state is described on the attempt row.</summary>
    RecreateFailed = 2,

    /// <summary>Docker host was unreachable at the start of the attempt;
    /// nothing was changed.</summary>
    HostUnreachable = 3,

    /// <summary>The configured container wasn't found on the host. Nothing
    /// was changed.</summary>
    ContainerNotFound = 4,
}
