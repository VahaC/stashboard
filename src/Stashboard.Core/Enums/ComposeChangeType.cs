namespace Stashboard.Core.Enums;

/// <summary>
/// V7.6 — what kind of Compose-file change a row in <c>ComposeChangeAudits</c>
/// records. Each save / restore / apply through the V7.6 diff-and-apply flow
/// writes one metadata-only audit row tagged with one of these so the Audit page
/// can show who changed which project, when, and which services were touched.
/// </summary>
public enum ComposeChangeType
{
    /// <summary>The whole Compose file was replaced from the Raw-YAML editor
    /// (validated + atomically renamed). <c>ChangedServices</c> lists the service
    /// keys whose definition changed.</summary>
    Save = 0,

    /// <summary>A previous on-disk revision was restored from
    /// <c>&lt;project&gt;/.stashboard/history/</c> (re-validated + written like any
    /// other save).</summary>
    Restore = 1,

    /// <summary>A <c>docker compose up -d</c> was run to apply the saved change to
    /// the running containers. <c>ChangedServices</c> lists the services it
    /// targeted (empty = the whole project). <c>Success</c> / <c>Error</c> carry
    /// the outcome.</summary>
    Apply = 2,
}
