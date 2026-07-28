using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V9.2 — the one-line decision both "Update now" endpoints share: if the watch
/// targets the Stashboard container itself, hand the recreate to a detached
/// self-update helper and report <see cref="DockerUpdateAttemptStatus.Scheduled"/>;
/// otherwise run the normal in-process recreate. Centralised so the two
/// controllers can't drift.
/// </summary>
internal static class SelfUpdateGate
{
    public static async Task<DockerUpdateResult> UpdateAsync(
        ISelfUpdateLauncher selfUpdateLauncher,
        IDockerImageUpdater imageUpdater,
        DockerUpdateProfile profile,
        CancellationToken cancellationToken)
    {
        if (await selfUpdateLauncher.IsSelfTargetAsync(profile, cancellationToken))
        {
            var launch = await selfUpdateLauncher.LaunchAsync(profile, cancellationToken);
            return launch.Started
                ? new DockerUpdateResult(DockerUpdateAttemptStatus.Scheduled, null, null,
                    "Stashboard is updating itself in a detached helper container. The UI will be "
                    + "briefly unavailable while it restarts — refresh in a minute.")
                : new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, null, null,
                    launch.Error ?? "Failed to start the self-update helper container.");
        }

        return await imageUpdater.UpdateAsync(profile, cancellationToken);
    }
}
