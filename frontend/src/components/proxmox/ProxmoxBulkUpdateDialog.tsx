import { useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertCircle, CheckCircle2, Loader2, RefreshCw, XCircle } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { applyProxmoxBulkUpdates, type ProxmoxUpdateStreamHandle } from '@/lib/proxmox-updates'
import { proxmoxQk } from '@/lib/proxmox-queries'
import { isBulkUpdateEligible } from '@/lib/proxmox-page'
import type { ProxmoxGuest } from '@/lib/types'
// The dialog chrome (update-progress-*, remove-confirm-warning) lives in the
// Docker page stylesheet; import it so the dialog carries its own styles.
import '@/styles/docker-instances.css'

type Phase =
  | { kind: 'confirm' }
  | { kind: 'running' }
  | { kind: 'done'; succeeded: number; failed: number }

/** Per-guest progress as the bulk run streams. */
type GuestState = {
  vmId: number
  name: string
  status: 'pending' | 'running' | 'success' | 'failed'
  nonDebian?: boolean
  error?: string | null
}

interface ProxmoxBulkUpdateDialogProps {
  connectionId: string
  /** The host's guests — node + LXCs (the node is offered too, see below). */
  guests: ProxmoxGuest[]
  onClose: () => void
}

/** The node row (`vmId 0`) vs an LXC. */
const isNodeVm = (vmId: number) => vmId === 0
/** Short tag shown after a guest's name: `node` for the host, else `CT <vmid>`. */
const guestTag = (vmId: number) => (isNodeVm(vmId) ? 'node' : `CT ${vmId}`)

/**
 * V6.11 — confirm → stream → result dialog for the host-wide bulk "Update now".
 * Reuses the single-guest {@link ProxmoxUpdateDialog} flow but iterates every
 * selected LXC: the operator picks which guests to update (eligible ones —
 * running, monitored, with pending updates — are pre-checked), then the apt log
 * for each streams in turn under a per-guest progress checklist.
 */
export function ProxmoxBulkUpdateDialog({ connectionId, guests, onClose }: ProxmoxBulkUpdateDialogProps) {
  const qc = useQueryClient()
  const eligible = useMemo(
    () => guests.filter((g) => isBulkUpdateEligible(g)).sort((a, b) => a.vmId - b.vmId), [guests])

  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [selected, setSelected] = useState<Set<number>>(() => new Set(eligible.map((g) => g.vmId)))
  const [states, setStates] = useState<GuestState[]>([])
  const [lines, setLines] = useState<string[]>([])
  const handleRef = useRef<ProxmoxUpdateStreamHandle | null>(null)
  const outputRef = useRef<HTMLPreElement | null>(null)

  useEffect(() => () => handleRef.current?.abort(), [])
  useEffect(() => {
    if (outputRef.current) outputRef.current.scrollTop = outputRef.current.scrollHeight
  }, [lines])

  const toggle = (vmId: number) =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(vmId)) next.delete(vmId); else next.add(vmId)
      return next
    })

  const start = () => {
    const vmIds = eligible.filter((g) => selected.has(g.vmId)).map((g) => g.vmId)
    if (vmIds.length === 0) return
    setLines([])
    setStates(vmIds.map((vmId) => ({
      vmId, name: eligible.find((g) => g.vmId === vmId)!.name, status: 'pending',
    })))
    setPhase({ kind: 'running' })

    handleRef.current = applyProxmoxBulkUpdates(connectionId, vmIds, (frame) => {
      if (frame.stream === 'guest-start') {
        setStates((prev) => prev.map((s) => s.vmId === frame.vmId ? { ...s, status: 'running' } : s))
        setLines((prev) => [...prev, `── ${frame.name} (CT ${frame.vmId}) ──`])
      } else if (frame.stream === 'stdout') {
        setLines((prev) => [...prev, frame.message])
      } else if (frame.stream === 'guest-end') {
        setStates((prev) => prev.map((s) => s.vmId === frame.vmId
          ? { ...s, status: frame.success ? 'success' : 'failed', nonDebian: frame.nonDebian, error: frame.error }
          : s))
      } else if (frame.stream === 'all-done') {
        setPhase({ kind: 'done', succeeded: frame.succeeded, failed: frame.failed })
        // The server recomputes each guest's pending count before the next
        // begins, so refetching now drops the counts + clears the badges.
        void qc.invalidateQueries({ queryKey: proxmoxQk.connections })
      } else {
        // error
        setLines((prev) => [...prev, frame.message])
        setPhase({ kind: 'done', succeeded: 0, failed: 0 })
      }
    })
  }

  const busy = phase.kind === 'running'
  const handleClose = () => { if (!busy) onClose() }
  const selectedCount = selected.size
  const nodeSelected = eligible.some((g) => isNodeVm(g.vmId) && selected.has(g.vmId))

  return (
    <Dialog open onOpenChange={(v) => { if (!v) handleClose() }}>
      <DialogContent className="update-progress-dialog">
        <DialogHeader>
          <DialogTitle>
            {phase.kind === 'confirm' && 'Update all'}
            {phase.kind === 'running' && 'Updating…'}
            {phase.kind === 'done' && (phase.failed === 0 ? 'Bulk update complete' : 'Bulk update finished with errors')}
          </DialogTitle>
          <DialogDescription className="sr-only">Apply pending package updates across the host's LXCs</DialogDescription>
        </DialogHeader>

        {phase.kind === 'confirm' && (
          eligible.length === 0 ? (
            <p className="update-progress-meta">
              Nothing has pending updates right now. Run <strong>Check now</strong> on the host first if you
              expect updates.
            </p>
          ) : (
            <>
              <p className="update-progress-meta">
                {selectedCount} of {eligible.length} target{eligible.length === 1 ? '' : 's'} selected · applied over SSH
              </p>
              <div className="proxmox-bulk-list">
                {eligible.map((g) => (
                  <label key={g.vmId} className="service-modal-checkbox-label proxmox-bulk-row">
                    <input type="checkbox" checked={selected.has(g.vmId)} onChange={() => toggle(g.vmId)} />
                    <span className="proxmox-bulk-name">{g.name} <code className="container-modal-code">{guestTag(g.vmId)}</code></span>
                    <span className="cc-update-badge">{g.pendingUpdates} update{g.pendingUpdates === 1 ? '' : 's'}</span>
                  </label>
                ))}
              </div>
              <p className="remove-confirm-warning warning-strong">
                <strong>Understand that this isn't risk-free.</strong> Stashboard runs <code>apt-get&nbsp;update</code> +
                <code> apt-get&nbsp;-y&nbsp;dist-upgrade</code> on each selected target in turn — inside a container via
                <code> pct&nbsp;exec</code>{nodeSelected ? <>, and on the <strong>node itself</strong> directly</> : null}.
                Applying upgrades as root can restart services; if a step fails part-way you may have to recover that target
                manually{nodeSelected ? <>, and a node upgrade may pull a new kernel that only takes effect after a manual <strong>reboot</strong></> : null}.
              </p>
            </>
          )
        )}

        {phase.kind !== 'confirm' && (
          <>
            <ul className="proxmox-bulk-progress">
              {states.map((s) => (
                <li key={s.vmId} data-status={s.status}>
                  {s.status === 'running' && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                  {s.status === 'success' && <CheckCircle2 className="h-3.5 w-3.5" />}
                  {s.status === 'failed' && <XCircle className="h-3.5 w-3.5" />}
                  {s.status === 'pending' && <span className="proxmox-bulk-dot" />}
                  <span className="proxmox-bulk-name">{s.name} <code className="container-modal-code">{guestTag(s.vmId)}</code></span>
                  {s.status === 'success' && s.nonDebian && <span className="proxmox-bulk-note">not apt-based</span>}
                  {s.status === 'failed' && <span className="proxmox-bulk-note">{s.error ?? 'failed'}</span>}
                </li>
              ))}
            </ul>
            <pre ref={outputRef} className="container-modal-raw" style={{ maxHeight: '260px', overflow: 'auto' }}>
              {lines.length > 0 ? lines.join('\n') : (busy ? 'Connecting…' : '(no output)')}
            </pre>
          </>
        )}

        {phase.kind === 'done' && (
          phase.failed === 0 ? (
            <p className="update-progress-banner update-progress-banner-success">
              <CheckCircle2 className="h-4 w-4" /> {phase.succeeded} target{phase.succeeded === 1 ? '' : 's'} upgraded.
            </p>
          ) : (
            <p className="update-progress-banner update-progress-banner-failed">
              <AlertCircle className="h-4 w-4" /> {phase.succeeded} succeeded, {phase.failed} failed — see the output above.
            </p>
          )
        )}

        <DialogFooter>
          {phase.kind === 'confirm' && (
            <>
              <Button type="button" variant="outline" onClick={handleClose}>Cancel</Button>
              <Button type="button" disabled={selectedCount === 0} onClick={start}>
                <RefreshCw className="h-3.5 w-3.5" /> Update {selectedCount > 0 ? selectedCount : ''} now
              </Button>
            </>
          )}
          {phase.kind === 'running' && (
            <Button type="button" variant="outline" disabled>
              <Loader2 className="h-3.5 w-3.5 animate-spin" /> Updating…
            </Button>
          )}
          {phase.kind === 'done' && (
            <Button type="button" onClick={handleClose}>Close</Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
