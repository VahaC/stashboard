import { useState } from 'react'
import { AlertCircle, History, Trash2 } from 'lucide-react'
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

export interface SnapshotConfirmDialogProps {
  /** Which destructive snapshot action is being confirmed. */
  action: 'rollback' | 'delete'
  vmId: number
  name: string
  snapshot: string
  onConfirm: () => Promise<void>
  onCancel: () => void
}

/**
 * V8.0 — double-confirm dialog for the two destructive snapshot actions, mirroring
 * the Docker {@link RemoveConfirmDialog} / {@link LxcDestroyDialog} verbatim (same
 * `remove-confirm-*` markup + CSS). A rollback **discards** any state newer than the
 * snapshot; a delete removes the restore point — naming the exact snapshot and
 * requiring the explicit click is the second of the safety gates (the global switch
 * and per-host opt-in are the first; the server re-checks both).
 */
export function SnapshotConfirmDialog({
  action, vmId, name, snapshot, onConfirm, onCancel,
}: SnapshotConfirmDialogProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const isRollback = action === 'rollback'

  const handleConfirm = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
    } catch (err: unknown) {
      setError(getApiErrorMessage(err) ?? `${isRollback ? 'Rollback' : 'Delete'} failed`)
      setBusy(false)
    }
  }

  const handleCancel = () => {
    if (busy) return
    setError(null)
    onCancel()
  }

  return (
    <Dialog open onOpenChange={(v) => { if (!v) handleCancel() }}>
      <DialogContent className="remove-confirm-dialog">
        <DialogHeader>
          <DialogTitle>{isRollback ? 'Roll back to snapshot?' : 'Delete snapshot?'}</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm {action} of snapshot {snapshot} on CT {vmId} {name}
          </DialogDescription>
        </DialogHeader>

        <dl className="remove-confirm-summary">
          <dt>Container</dt>
          <dd>CT {vmId} · {name}</dd>
          <dt>Snapshot</dt>
          <dd>{snapshot}</dd>
        </dl>

        <p className="remove-confirm-warning">
          {isRollback
            ? 'Rolling back reverts the container to this snapshot and discards any state newer than it. This cannot be undone.'
            : 'This permanently removes the snapshot. The container itself is unaffected, but the restore point is gone for good.'}
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
            {isRollback ? <History className="h-3.5 w-3.5" /> : <Trash2 className="h-3.5 w-3.5" />}
            {busy ? (isRollback ? 'Rolling back…' : 'Deleting…') : (isRollback ? 'Roll back' : 'Delete snapshot')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
