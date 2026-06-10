import { useEffect, useRef, useState } from 'react'
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
import { applyProxmoxUpdates, type ProxmoxUpdateStreamHandle } from '@/lib/proxmox-updates'
import { proxmoxQk } from '@/lib/proxmox-queries'
// The dialog chrome (update-progress-*, remove-confirm-warning) lives in the
// Docker page stylesheet; import it so the dialog carries its own styles
// regardless of which surface (Proxmox card / LXC modal) opens it.
import '@/styles/docker-instances.css'

type Phase =
  | { kind: 'confirm' }
  | { kind: 'running' }
  | { kind: 'done'; success: boolean; error: string | null; nonDebian: boolean }

interface ProxmoxUpdateDialogProps {
  connectionId: string
  /** `0` upgrades the node; any other value upgrades that LXC. */
  vmId: number
  /** Human label for the target shown in the dialog (e.g. `pihole (CT 105)` or the node name). */
  targetLabel: string
  onClose: () => void
}

/**
 * V6.7.1 — confirm → stream → result dialog for one-click "Update now". Mirrors
 * the Docker {@link UpdateProgressDialog} state machine, but instead of a
 * per-target checklist it shows the live apt output as it streams from the host
 * (NDJSON over fetch). Applying updates is privileged + slow, so the confirm
 * step is explicit and the node-wide caveat is spelled out.
 */
export function ProxmoxUpdateDialog({ connectionId, vmId, targetLabel, onClose }: ProxmoxUpdateDialogProps) {
  const qc = useQueryClient()
  const isNode = vmId === 0
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })
  const [lines, setLines] = useState<string[]>([])
  const handleRef = useRef<ProxmoxUpdateStreamHandle | null>(null)
  const outputRef = useRef<HTMLPreElement | null>(null)

  // Tidy up the stream if the dialog unmounts mid-run.
  useEffect(() => () => handleRef.current?.abort(), [])

  // Keep the latest output line in view.
  useEffect(() => {
    if (outputRef.current) outputRef.current.scrollTop = outputRef.current.scrollHeight
  }, [lines])

  const start = () => {
    setLines([])
    setPhase({ kind: 'running' })
    handleRef.current = applyProxmoxUpdates(connectionId, vmId, (frame) => {
      if (frame.stream === 'stdout') {
        setLines((prev) => [...prev, frame.message])
      } else if (frame.stream === 'error') {
        setLines((prev) => [...prev, frame.message])
        setPhase({ kind: 'done', success: false, error: frame.message, nonDebian: false })
      } else {
        // end
        setPhase({ kind: 'done', success: frame.success, error: frame.error, nonDebian: frame.nonDebian })
        // The server recomputes this target's pending count before sending the
        // `end` frame, so refetching the host now drops the count + clears the
        // "updates pending" badge (mirrors Docker's auto-recheck after update).
        void qc.invalidateQueries({ queryKey: proxmoxQk.connections })
      }
    })
  }

  const busy = phase.kind === 'running'
  const handleClose = () => { if (!busy) onClose() }

  return (
    <Dialog open onOpenChange={(v) => { if (!v) handleClose() }}>
      <DialogContent className="update-progress-dialog">
        <DialogHeader>
          <DialogTitle>
            {phase.kind === 'confirm' && <>Update <code className="container-modal-code">{targetLabel}</code></>}
            {phase.kind === 'running' && <>Updating <code className="container-modal-code">{targetLabel}</code>…</>}
            {phase.kind === 'done' && (
              phase.success
                ? <>Update complete: <code className="container-modal-code">{targetLabel}</code></>
                : <>Update finished with errors: <code className="container-modal-code">{targetLabel}</code></>
            )}
          </DialogTitle>
          <DialogDescription className="sr-only">Apply pending package updates for {targetLabel}</DialogDescription>
        </DialogHeader>

        {phase.kind === 'confirm' && (
          <>
            <p className="update-progress-meta">
              {isNode ? 'Node upgrade' : '1 container'} · applied over SSH
            </p>

            <p className="remove-confirm-warning">
              Stashboard will run <code>apt-get&nbsp;update</code> + <code>apt-get&nbsp;-y&nbsp;dist-upgrade</code> over SSH
              {isNode
                ? <> on the <strong>whole node</strong> — Proxmox has no per-container probe, so a node upgrade applies to the node itself, not a single guest.</>
                : <> inside this container (<code>pct&nbsp;exec</code>).</>}
              {' '}The apt output streams live below as it runs.
            </p>
            <p className="remove-confirm-warning warning-strong">
              <strong>Understand that this isn't risk-free.</strong> Applying package upgrades as root can restart
              services{isNode ? <>, and a node upgrade may pull a new kernel that only takes effect after a <strong>manual reboot</strong></> : null}.
              If a step fails part-way you may have to recover the {isNode ? 'node' : 'container'} manually. Please make
              sure you know what this does before continuing.
            </p>
          </>
        )}

        {phase.kind === 'done' && (
          phase.success
            ? (
              <p className="update-progress-banner update-progress-banner-success">
                <CheckCircle2 className="h-4 w-4" />
                {phase.nonDebian
                  ? <>Nothing to upgrade — <code className="container-modal-code">{targetLabel}</code> is not a Debian/apt target.</>
                  : <><code className="container-modal-code">{targetLabel}</code> upgraded.</>}
              </p>
            )
            : (
              <p className="update-progress-banner update-progress-banner-failed">
                <AlertCircle className="h-4 w-4" /> {phase.error ?? 'The upgrade failed — see the output below.'}
              </p>
            )
        )}

        {(phase.kind === 'running' || phase.kind === 'done') && (
          <pre ref={outputRef} className="container-modal-raw" style={{ maxHeight: '320px', overflow: 'auto' }}>
            {lines.length > 0 ? lines.join('\n') : (busy ? 'Connecting…' : '(no output)')}
          </pre>
        )}

        <DialogFooter>
          {phase.kind === 'confirm' && (
            <>
              <Button type="button" variant="outline" onClick={handleClose}>Cancel</Button>
              <Button type="button" onClick={start}>
                <RefreshCw className="h-3.5 w-3.5" /> Update now
              </Button>
            </>
          )}
          {phase.kind === 'running' && (
            <Button type="button" variant="outline" disabled>
              <Loader2 className="h-3.5 w-3.5 animate-spin" /> Updating…
            </Button>
          )}
          {phase.kind === 'done' && (
            <Button type="button" onClick={handleClose}>
              {phase.success ? <CheckCircle2 className="h-3.5 w-3.5" /> : <XCircle className="h-3.5 w-3.5" />} Close
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
