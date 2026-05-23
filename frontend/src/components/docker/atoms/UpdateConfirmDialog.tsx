import { useState } from 'react'
import { AlertCircle, Download, ExternalLink } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { getApiErrorMessage } from '@/lib/utils'

/** Where the "how Update now works + its limitations" docs live. */
const UPDATE_DOCS_URL =
  'https://github.com/VahaC/stashboard/blob/main/DOCKER_UPDATE_MONITORING_GUIDE.md#51-update-now-v27'

export interface UpdateConfirmDialogProps {
  open: boolean
  imageReference: string
  containerName: string
  /** True when the watch's status is `UpdateAvailable` (vs. a plain re-pull). */
  updateAvailable: boolean
  onConfirm: () => Promise<void>
  onCancel: () => void
}

/**
 * V5.2 — confirmation dialog for the "Update now" button. Replaces the old
 * native `confirm()` so we can spell out that recreating a running container
 * is not risk-free and link to the documentation. The action only runs when
 * the user explicitly confirms here.
 */
export function UpdateConfirmDialog({
  open, imageReference, containerName, updateAvailable, onConfirm, onCancel,
}: UpdateConfirmDialogProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleConfirm = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
      // Success — the parent closes the dialog. Reset busy so a later reopen
      // (the watch lives on after an update) isn't stuck on "Updating…".
      setBusy(false)
    } catch (err: unknown) {
      setError(getApiErrorMessage(err) ?? 'Update failed')
      setBusy(false)
    }
  }

  const handleCancel = () => {
    if (busy) return
    setError(null)
    onCancel()
  }

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) handleCancel() }}>
      <DialogContent className="remove-confirm-dialog">
        <DialogHeader>
          <DialogTitle>Update now?</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm pulling a new image and recreating container {containerName}
          </DialogDescription>
        </DialogHeader>

        <dl className="remove-confirm-summary">
          <dt>Image</dt>
          <dd><code className="container-modal-code">{imageReference}</code></dd>
          <dt>Container</dt>
          <dd>{containerName}</dd>
        </dl>

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

        <p>
          <a
            href={UPDATE_DOCS_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="docker-release-panel-link"
          >
            Read how “Update now” works and its limitations
            <ExternalLink className="h-3 w-3 inline ml-1" />
          </a>
        </p>

        {error && (
          <p className="remove-confirm-error">
            <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={handleCancel}>
            Cancel
          </Button>
          <Button type="button" variant="default" disabled={busy} onClick={handleConfirm}>
            <Download className="h-3.5 w-3.5" />
            {busy ? 'Updating…' : 'Update now'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
