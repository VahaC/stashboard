import type { DockerWatchUpdateResponse } from '@/lib/types'
import {
  UpdateProgressDialog,
  type UpdateOutcome,
} from './UpdateProgressDialog'

/** Where the "how Update now works + its limitations" docs live. */
const UPDATE_DOCS_URL =
  'https://github.com/VahaC/stashboard/blob/main/DOCKER_UPDATE_MONITORING_GUIDE.md#51-update-now-v27'

export interface UpdateConfirmDialogProps {
  open: boolean
  imageReference: string
  containerName: string
  /** True when the watch's status is `UpdateAvailable` (vs. a plain re-pull). */
  updateAvailable: boolean
  /** Runs the actual update and returns the per-watch attempt envelope so
   *  the dialog can render ✓ / ✗ in the unified done phase. Throwing
   *  surfaces the error on the row. */
  onConfirm: () => Promise<DockerWatchUpdateResponse>
  onClose: () => void
}

/**
 * V5.2 — confirmation + progress dialog for the per-watch "Update now"
 * button. V5.4 — refactored to a thin adapter over the unified
 * <see cref="UpdateProgressDialog"/> so the per-container and per-project
 * flows share the same confirm → running → done/error UX.
 */
export function UpdateConfirmDialog({
  open, imageReference, containerName, updateAvailable, onConfirm, onClose,
}: UpdateConfirmDialogProps) {
  const target = {
    key: containerName,
    displayName: containerName,
    secondary: null,
    image: imageReference,
    // Per-watch "Update now" only fires for a tracked container — the
    // mutation lives on the watch itself.
    tracked: true,
  }

  const handleConfirm = async (): Promise<UpdateOutcome[]> => {
    const response = await onConfirm()
    const status = response.attempt.status
    // V9.2 — Stashboard updating itself is offloaded to a detached helper;
    // the recreate runs out of band, so render a distinct "Scheduled" state
    // rather than a green ✓ (which would imply it's already done) or a red ✗.
    const scheduled = status === 'Scheduled' || status === 5
    const success = scheduled || status === 'Success' || status === 0
    return [{
      key: containerName,
      success,
      scheduled,
      error: response.attempt.error,
    }]
  }

  return (
    <UpdateProgressDialog
      open={open}
      scope="container"
      subject={containerName}
      targets={[target]}
      description={
        <>
          <p className="remove-confirm-warning">
            Stashboard will {updateAvailable ? 'pull the new image' : 're-pull the configured tag'} and
            recreate this container. Depending on the connection's settings this runs either a raw
            recreate (stop → remove → create → start) or a Compose-aware recreate
            (<code>docker&nbsp;compose&nbsp;pull</code> + <code>up&nbsp;-d</code>).
          </p>
          <p className="remove-confirm-warning warning-strong">
            <strong>Understand that this isn't risk-free.</strong> Recreating a running container causes
            brief downtime, and if a step fails after the old container is removed you may have to
            recover it manually. Please make sure you know what this does before continuing.
          </p>
        </>
      }
      docs={{ href: UPDATE_DOCS_URL, label: 'Read how “Update now” works and its limitations' }}
      onConfirm={handleConfirm}
      onClose={onClose}
    />
  )
}
