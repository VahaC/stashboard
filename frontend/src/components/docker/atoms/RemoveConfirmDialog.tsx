import { useState } from 'react'
import { AlertCircle, Trash2 } from 'lucide-react'
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

export interface RemoveConfirmDialogProps {
  open: boolean
  containerName: string
  image: string
  status: string
  /** True for tracked containers that no longer exist on the Docker host. */
  ghost?: boolean
  onConfirm: () => Promise<void>
  onCancel: () => void
}

export function RemoveConfirmDialog({
  open, containerName, image, status, ghost, onConfirm, onCancel,
}: RemoveConfirmDialogProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleConfirm = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
    } catch (err: unknown) {
      setError(getApiErrorMessage(err) ?? 'Remove failed')
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

        {error && (
          <p className="remove-confirm-error">
            <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={handleCancel}>
            Cancel
          </Button>
          <Button type="button" variant="destructive" disabled={busy} onClick={handleConfirm}>
            <Trash2 className="h-3.5 w-3.5" />
            {busy ? 'Removing…' : ghost ? 'Remove tracking' : 'Remove container'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
