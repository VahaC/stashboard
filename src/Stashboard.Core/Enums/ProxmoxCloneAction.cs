namespace Stashboard.Core.Enums;

/// <summary>
/// V8.0 — which clone/snapshot lifecycle action an audit row records. Persisted as
/// a string (see the DbContext conversion) so the audit history stays readable.
/// </summary>
public enum ProxmoxCloneAction
{
    /// <summary>Cloned an existing guest into a new one
    /// (<c>POST …/lxc/{vmid}/clone</c>).</summary>
    Clone,

    /// <summary>Took a snapshot (<c>POST …/lxc/{vmid}/snapshot</c>).</summary>
    SnapshotCreate,

    /// <summary>Rolled back to a snapshot
    /// (<c>POST …/lxc/{vmid}/snapshot/{name}/rollback</c>) — discards newer state.</summary>
    SnapshotRollback,

    /// <summary>Deleted a snapshot
    /// (<c>DELETE …/lxc/{vmid}/snapshot/{name}</c>).</summary>
    SnapshotDelete,
}
