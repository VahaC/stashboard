namespace Stashboard.Core.Enums;

/// <summary>
/// V3.5 — discriminator for the kind of lifecycle action a row in
/// <c>DockerUpdateAttempts</c> represents. The audit table doubles as the
/// activity log for both the per-watch "Update now" button and the
/// container-management actions on the V3.5 Docker instances page, so each
/// row tags itself with one of these values.
/// </summary>
public enum DockerContainerActionType
{
    /// <summary>V2.7 — pull + recreate. The original "Update now" flow.</summary>
    Update = 0,

    /// <summary>V3.5 — <c>docker start &lt;container&gt;</c>.</summary>
    Start = 1,

    /// <summary>V3.5 — <c>docker stop &lt;container&gt;</c>.</summary>
    Stop = 2,

    /// <summary>V3.5 — <c>docker restart &lt;container&gt;</c>.</summary>
    Restart = 3,

    /// <summary>V3.5 — <c>docker rm &lt;container&gt;</c>. Gated by the
    /// server-side <c>Stashboard.AllowContainerRemoval</c> feature flag.</summary>
    Remove = 4,
}
