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

export interface LxcDestroyDialogProps {
  open: boolean
  vmId: number
  name: string
  nodeName: string
  /** V6.14 — a QEMU VM vs an LXC container; only changes the wording. */
  isVm?: boolean
  onConfirm: () => Promise<void>
  onCancel: () => void
}

/**
 * V6.13 — double-confirm dialog for destroying an LXC (V6.14 — or a QEMU VM),
 * mirroring the Docker {@link RemoveConfirmDialog} verbatim (same
 * `remove-confirm-*` markup + CSS). Naming the exact guest
 * (`CT <vmid> · <name>` / `VM <vmid> · <name>`) and requiring the explicit
 * destructive click is the second of the safety gates — the global switch and
 * per-host opt-in are the first; the server re-checks all of them.
 */
export function LxcDestroyDialog({
  open, vmId, name, nodeName, isVm = false, onConfirm, onCancel,
}: LxcDestroyDialogProps) {
  const noun = isVm ? 'VM' : 'container'
  const label = isVm ? `VM ${vmId}` : `CT ${vmId}`
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleConfirm = async () => {
    setBusy(true)
    setError(null)
    try {
      await onConfirm()
    } catch (err: unknown) {
      setError(getApiErrorMessage(err) ?? 'Destroy failed')
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
          <DialogTitle>Destroy {noun}?</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm destroying {noun} {label} {name}
          </DialogDescription>
        </DialogHeader>

        <dl className="remove-confirm-summary">
          <dt>{isVm ? 'Virtual machine' : 'Container'}</dt>
          <dd>{label} · {name}</dd>
          <dt>Node</dt>
          <dd>{nodeName}</dd>
        </dl>

        <p className="remove-confirm-warning">
          This permanently destroys the {noun} and its {isVm ? 'disks' : 'disk'} on the Proxmox host. This cannot be undone.
          Associated backups and external storage volumes are NOT removed.
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
            {busy ? 'Destroying…' : `Destroy ${noun}`}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
