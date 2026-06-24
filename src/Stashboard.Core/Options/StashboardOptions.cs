namespace Stashboard.Core.Options;

/// <summary>
/// Top-level app feature flags. Bound from the <c>Stashboard</c> config
/// section (or <c>STASHBOARD_Stashboard__*</c> environment variables) at
/// startup. Defaults are deliberately conservative — destructive features
/// stay off unless the operator opts in.
/// </summary>
public sealed class StashboardOptions
{
    public const string SectionName = "Stashboard";

    /// <summary>
    /// V3.5 — when <c>true</c>, the Docker instances page renders the
    /// "Remove container" action and the
    /// <c>DELETE /api/docker/connections/{id}/containers/{name}</c>
    /// endpoint executes the daemon call. Default is <c>false</c>:
    /// removing a container is irreversible from the UI (the
    /// container's writable layer is gone), so it has to be turned on
    /// deliberately by the operator. The UI still gates the action
    /// behind a second confirmation when the flag is enabled.
    /// </summary>
    public bool AllowContainerRemoval { get; set; } = false;

    /// <summary>
    /// V5.3 — <strong>first-run seed</strong> for the browser host terminal
    /// master switch. The live value is DB-backed and managed from the Settings
    /// page (<c>HostShellSettingsEntity</c> / <c>/api/settings/host-shell</c>);
    /// this config flag only seeds the row the first time it's read, so an
    /// operator who set it on first boot keeps that value until they change it
    /// in the UI. Default <c>false</c> — the host terminal is the most dangerous
    /// surface in the product (host-level RCE) and also requires a per-connection
    /// opt-in (<c>DockerConnection.AllowHostShell</c>) and an SSH connection.
    /// </summary>
    public bool AllowHostShell { get; set; } = false;

    /// <summary>
    /// V5.7 — <strong>first-run seed</strong> for the container-exec master
    /// switch. The live value is DB-backed and managed from the Settings page
    /// (<c>ContainerExecSettingsEntity</c> / <c>/api/settings/container-exec</c>);
    /// this config flag only seeds the row the first time it's read, so an
    /// operator who set it on first boot keeps that value until they change it
    /// in the UI. Default <c>false</c> — opening a shell inside a container is
    /// sensitive (it runs arbitrary commands in the workload), so it also
    /// requires a per-connection opt-in (<c>DockerConnection.AllowExec</c>).
    /// Unlike the host terminal, exec runs through the Docker daemon, so it
    /// works for every host type (local socket, TCP+TLS, SSH tunnel).
    /// </summary>
    public bool AllowContainerExec { get; set; } = false;

    /// <summary>
    /// V6.6 — <strong>first-run seed</strong> for the browser Proxmox LXC
    /// console master switch. The live value is DB-backed and managed from the
    /// Settings page (<c>ProxmoxConsoleSettingsEntity</c> /
    /// <c>/api/settings/proxmox-console</c>); this config flag only seeds the
    /// row the first time it's read, so an operator who set it on first boot
    /// keeps that value until they change it in the UI. Default <c>false</c> —
    /// the console SSHes to the Proxmox host and runs <c>pct exec</c> inside a
    /// guest, so it also requires a per-host opt-in
    /// (<c>ProxmoxConnection.AllowConsole</c>) and SSH credentials on the host.
    /// </summary>
    public bool AllowProxmoxConsole { get; set; } = false;

    /// <summary>
    /// V6.7.1 — <strong>first-run seed</strong> for the one-click Proxmox
    /// <em>Update now</em> master switch. The live value is DB-backed and
    /// managed from the Settings page (<c>ProxmoxUpdateApplySettingsEntity</c> /
    /// <c>/api/settings/proxmox-updates</c>); this config flag only seeds the row
    /// the first time it's read, so an operator who set it on first boot keeps
    /// that value until they change it in the UI. Default <c>false</c> — applying
    /// updates runs <c>apt-get dist-upgrade</c> on the node and inside its LXCs
    /// over SSH, so it also requires a per-host opt-in
    /// (<c>ProxmoxConnection.AllowUpdates</c>) and SSH credentials on the host.
    /// </summary>
    public bool AllowProxmoxUpdates { get; set; } = false;

    /// <summary>
    /// V6.13 — <strong>first-run seed</strong> for the destroy-LXC master switch.
    /// The live value is DB-backed and managed from the Settings page
    /// (<c>ProxmoxDestroySettingsEntity</c> /
    /// <c>/api/settings/proxmox-destroy</c>); this config flag only seeds the row
    /// the first time it's read, so an operator who set it on first boot keeps
    /// that value until they change it in the UI. Default <c>false</c> —
    /// destroying an LXC is irreversible (the container's disk is gone), so it
    /// also requires a per-host opt-in (<c>ProxmoxConnection.AllowDestroy</c>) and
    /// a double confirmation in the UI before it can run.
    /// </summary>
    public bool AllowProxmoxDestroy { get; set; } = false;

    /// <summary>
    /// V6.13.1 — <strong>first-run seed</strong> for the create-LXC master switch.
    /// The live value is DB-backed and managed from the Settings page
    /// (<c>ProxmoxCreateSettingsEntity</c> /
    /// <c>/api/settings/proxmox-create</c>); this config flag only seeds the row
    /// the first time it's read, so an operator who set it on first run keeps that
    /// value until they change it in the UI. Default <c>false</c> — creating an LXC
    /// provisions a new container from a template on the host, so it also requires
    /// a per-host opt-in (<c>ProxmoxConnection.AllowCreate</c>) before it can run.
    /// </summary>
    public bool AllowProxmoxCreate { get; set; } = false;

    /// <summary>
    /// V8.0 — <strong>first-run seed</strong> for the clone/snapshot master switch.
    /// The live value is DB-backed and managed from the Settings page
    /// (<c>ProxmoxCloneSettingsEntity</c> / <c>/api/settings/proxmox-clone</c>);
    /// this config flag only seeds the row the first time it's read, so an operator
    /// who set it on first run keeps that value until they change it in the UI.
    /// Default <c>false</c> — cloning a guest and rolling back a snapshot both write
    /// real state on the host, so it also requires a per-host opt-in
    /// (<c>ProxmoxConnection.AllowClone</c>) before it can run.
    /// </summary>
    public bool AllowProxmoxClone { get; set; } = false;
}
