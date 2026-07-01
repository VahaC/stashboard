import { useState } from 'react'
import { AlertCircle, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { getApiErrorMessage } from '@/lib/utils'

export interface RemoveConfirmDialogProps {
  open: boolean
  containerName: string
  image: string
  status: string
  /** True for tracked containers that no longer exist on the Docker host. */
  ghost?: boolean
  /** V8.2 — the container's compose project name, when one exists AND the
   *  connection is SSH-based (the only transport that can delete host files).
   *  Non-null shows the "also delete the project folder" opt-in. */
  composeProjectName?: string | null
  onConfirm: (options: { deleteProjectFolder: boolean }) => Promise<void>
  onCancel: () => void
}

const CONFIRM_WORD = 'delete'

export function RemoveConfirmDialog({
  open, containerName, image, status, ghost, composeProjectName, onConfirm, onCancel,
}: RemoveConfirmDialogProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [deleteProjectFolder, setDeleteProjectFolder] = useState(false)
  const [confirmText, setConfirmText] = useState('')

  // Ghost rows just drop a tracking entry — nothing destructive happens on
  // the Docker host, so the typed-confirmation gate only applies to an
  // actual container (and folder) removal.
  const requiresTypedConfirm = !ghost
  const isConfirmed = !requiresTypedConfirm || confirmText.trim().toLowerCase() === CONFIRM_WORD

  const handleConfirm = async () => {
    if (!isConfirmed) return
    setBusy(true)
    setError(null)
    try {
      await onConfirm({ deleteProjectFolder })
    } catch (err: unknown) {
      setError(getApiErrorMessage(err) ?? 'Remove failed')
      setBusy(false)
    }
  }

  const handleCancel = () => {
    if (busy) return
    setError(null)
    setDeleteProjectFolder(false)
    setConfirmText('')
    onCancel()
  }

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) handleCancel() }}>
      <DialogContent className="remove-confirm-dialog">
        <DialogHeader>
          <DialogTitle>{ghost ? 'Remove tracking?' : 'Remove container?'}</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm removal of container {containerName}
          </DialogDescription>
        </DialogHeader>

        <dl className="remove-confirm-summary">
          <dt>Container</dt>
          <dd>{containerName}</dd>
          <dt>Image</dt>
          <dd><code className="container-modal-code">{image}</code></dd>
          <dt>Status</dt>
          <dd>{status}</dd>
        </dl>

        <p className="remove-confirm-warning">
          {ghost
            ? 'The container no longer exists on the Docker host. This will remove its tracking entry from Stashboard.'
            : 'This will permanently remove the container. The image will NOT be deleted.'}
        </p>

        {composeProjectName && (
          <label className="remove-confirm-folder-check">
            <input
              type="checkbox"
              checked={deleteProjectFolder}
              onChange={(e) => setDeleteProjectFolder(e.target.checked)}
            />
            <span>
              Also delete the compose project folder for <strong>{composeProjectName}</strong> on the
              host — <code className="container-modal-code">docker-compose.yml</code> and the data for{' '}
              <strong>all services</strong> in this project. This cannot be undone.
            </span>
          </label>
        )}

        {requiresTypedConfirm && (
          <div className="remove-confirm-type">
            <label htmlFor="remove-confirm-type-input">
              Type <strong>{CONFIRM_WORD}</strong> to confirm
            </label>
            <Input
              id="remove-confirm-type-input"
              type="text"
              autoComplete="off"
              autoCorrect="off"
              autoCapitalize="off"
              spellCheck={false}
              value={confirmText}
              onChange={(e) => setConfirmText(e.target.value)}
              placeholder={CONFIRM_WORD}
            />
          </div>
        )}

        {error && (
          <p className="remove-confirm-error">
            <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={handleCancel}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" disabled={busy || !isConfirmed} onClick={handleConfirm}>
            <Trash2 className="h-3.5 w-3.5" />
            {busy ? 'Removing…' : ghost ? 'Remove tracking' : 'Remove container'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
