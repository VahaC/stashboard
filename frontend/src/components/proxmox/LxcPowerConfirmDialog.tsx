import { Power, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'

/** The two power-off verbs this dialog explains + confirms. Start / Reboot run
 *  directly (no confirmation) — only the two ways to *stop* a guest, which differ
 *  in safety, get a confirm + explanation. */
export type LxcPowerAction = 'stop' | 'shutdown'

export interface LxcPowerConfirmDialogProps {
  open: boolean
  action: LxcPowerAction
  vmId: number
  name: string
  /** A QEMU VM vs an LXC container — only changes the wording. */
  isVm: boolean
  /** The lifecycle mutation is in flight (disables the buttons). */
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/**
 * V6.14 — confirm dialog for the two "power off" verbs, spelling out **what each
 * one actually does** so the difference between a graceful **Shutdown** and a
 * hard **Stop** is clear before the click. Reuses the destroy dialog's
 * `remove-confirm-*` markup + CSS verbatim (same surface, not a parallel one).
 *
 * - **Shutdown** is graceful and safe — an ACPI / guest-agent request the guest
 *   OS handles cleanly; it can take a moment or be ignored by a guest with no
 *   agent, so the card stays "running" until it actually stops.
 * - **Stop** is a hard power-off — immediate, with possible data loss / a dirty
 *   filesystem — so its confirm button is danger-styled.
 */
export function LxcPowerConfirmDialog({
  open, action, vmId, name, isVm, busy = false, onConfirm, onCancel,
}: LxcPowerConfirmDialogProps) {
  const noun = isVm ? 'VM' : 'container'
  const label = isVm ? `VM ${vmId}` : `CT ${vmId}`
  const isStop = action === 'stop'

  const handleCancel = () => { if (!busy) onCancel() }

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) handleCancel() }}>
      <DialogContent className="remove-confirm-dialog">
        <DialogHeader>
          <DialogTitle>{isStop ? `Stop ${noun}?` : `Shut down ${noun}?`}</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm {isStop ? 'stopping' : 'shutting down'} {label} {name}
          </DialogDescription>
        </DialogHeader>

        <dl className="remove-confirm-summary">
          <dt>{isVm ? 'Virtual machine' : 'Container'}</dt>
          <dd>{label} · {name}</dd>
          <dt>Action</dt>
          <dd>{isStop ? 'Stop — hard power-off' : 'Shutdown — graceful'}</dd>
        </dl>

        {isStop ? (
          <p className="remove-confirm-warning">
            <strong className="warning-strong">Stop forces the {noun} off immediately</strong> — like
            pulling the power cord. It does <strong>not</strong> wait for the operating system, so
            <strong className="warning-strong"> unsaved data can be lost</strong> and the filesystem may
            be left in a dirty state. Prefer <strong>Shutdown</strong> for a clean stop; use this only
            when a graceful shutdown won't work
            {isVm ? ' (e.g. a VM with no guest agent that ignores the shutdown request).' : '.'}
          </p>
        ) : (
          <p className="remove-confirm-warning">
            <strong>Shutdown</strong> asks the {noun}'s operating system to power off cleanly — an
            ACPI / guest-agent request, the same as choosing “Shut down” inside it. No data is lost.
            It can take a little while, and a {noun}
            {isVm ? ' with no guest agent' : ''} that ignores the request may not stop — the card keeps
            showing “running” until it actually does. Use <strong>Stop</strong> if it won't shut down.
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" disabled={busy} onClick={handleCancel}>
            Cancel
          </Button>
          <Button
            type="button"
            variant={isStop ? 'destructive' : 'default'}
            disabled={busy}
            onClick={onConfirm}
          >
            {isStop ? <Square className="h-3.5 w-3.5" /> : <Power className="h-3.5 w-3.5" />}
            {isStop ? 'Stop' : 'Shutdown'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
