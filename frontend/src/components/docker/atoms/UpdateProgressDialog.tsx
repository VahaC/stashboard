import { useState, type ReactNode } from 'react'
import {
  AlertCircle,
  CheckCircle2,
  Clock,
  ExternalLink,
  Loader2,
  RefreshCw,
  XCircle,
} from 'lucide-react'
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

/**
 * V5.4 — one update target row inside the dialog. Both the per-container
 * "Update now" flow and the per-project bulk "Update project" flow feed
 * the same shape — the only difference is N=1 vs. N=services.
 */
export interface UpdateTarget {
  /** Stable key used to match outcomes back to rows. Usually the Docker
   *  container name. */
  key: string
  /** Display name shown on the row. Service name for compose; container
   *  name otherwise. */
  displayName: string
  /** Optional secondary tag shown in parentheses after the display name —
   *  useful when display name differs from container name (compose service
   *  vs. project_service_1 container). */
  secondary?: string | null
  image: string
  /** True when Stashboard is tracking the container with a watch. Drives
   *  the "anonymous pull only" hint in the confirm phase. */
  tracked: boolean
}

/**
 * Outcome of a single target after the update has run. The dialog renders
 * ✓ Updated or ✗ Failed (with the error inline) per row from this list.
 */
export interface UpdateOutcome {
  /** Must match the `key` of the corresponding `UpdateTarget`. */
  key: string
  success: boolean
  error?: string | null
  /** V9.2 — the target is the Stashboard container itself, so the recreate
   *  was handed off to a detached helper. Not a success or a failure: the
   *  app is about to restart out of band. Rendered as a distinct
   *  "Scheduled" state with an informational banner. */
  scheduled?: boolean
}

export interface UpdateProgressDialogProps {
  open: boolean
  /** Bare-word subject ("container", "project") used to compose phase
   *  titles. */
  scope: 'container' | 'project'
  /** Identifier shown in the dialog title (container name, project name…). */
  subject: string
  /** One-or-more rows the dialog ticks off. */
  targets: UpdateTarget[]
  /** Warning copy shown in the confirm phase, above the row list. */
  description: ReactNode
  /** Optional docs link rendered under the row list during confirm. */
  docs?: { href: string; label: string }
  /** Async callback that runs the actual update and resolves with one
   *  outcome per target. Throwing here puts the dialog in the `error`
   *  phase. */
  onConfirm: () => Promise<UpdateOutcome[]>
  /** Called when the user dismisses the dialog. Ignored while a run is in
   *  flight. */
  onClose: () => void
  /** Optional extra content for the success / failure banner — e.g. the
   *  compose-vs-recreate mode hint on the bulk update. */
  buildSummary?: (outcomes: UpdateOutcome[]) => { success?: ReactNode; failure?: ReactNode }
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'running' }
  | { kind: 'done'; outcomes: UpdateOutcome[] }
  | { kind: 'error'; message: string }

/**
 * V5.4 — shared confirm-run-result dialog used by both the per-container
 * "Update now" button on a watch and the per-project "Update project"
 * button on the Docker instances page. One target → one row + container
 * phrasing; many targets → checklist + project phrasing. The phase state
 * machine (`confirm → running → done/error`) is identical so users see the
 * same shape regardless of which surface they came from.
 */
export function UpdateProgressDialog({
  open, scope, subject, targets, description, docs, onConfirm, onClose, buildSummary,
}: UpdateProgressDialogProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'confirm' })

  // Reset to confirm whenever the dialog (re)opens so a previous run's
  // checklist doesn't shadow a fresh confirmation. Done during render off the
  // open transition (not in an effect) — the React-documented "you might not
  // need an effect" reset pattern, which avoids the cascading-render lint.
  const [wasOpen, setWasOpen] = useState(open)
  if (open !== wasOpen) {
    setWasOpen(open)
    if (open) setPhase({ kind: 'confirm' })
  }

  const outcomesByKey = new Map<string, UpdateOutcome>()
  if (phase.kind === 'done') {
    for (const o of phase.outcomes) outcomesByKey.set(o.key, o)
  }

  const busy = phase.kind === 'running'
  const isProject = scope === 'project'
  const trackedCount = targets.filter((t) => t.tracked).length

  const handleConfirm = async () => {
    setPhase({ kind: 'running' })
    try {
      const outcomes = await onConfirm()
      setPhase({ kind: 'done', outcomes })
    } catch (err) {
      // Mirrors the project dialog: try the backend's `{ error: "..." }`
      // envelope first, fall back to the HTTP status + a sensible default.
      const status = (err as { response?: { status?: number } })?.response?.status
      const parsed = getApiErrorMessage(err)
      const fallback = `Failed to update ${scope} ${subject}.`
      const message = parsed && parsed !== 'An error occurred.'
        ? parsed
        : status
          ? `${fallback.slice(0, -1)} (HTTP ${status}).`
          : fallback
      setPhase({ kind: 'error', message })
    }
  }

  const handleClose = () => {
    if (busy) return
    onClose()
  }

  // ── per-target status pill ─────────────────────────────────────────────
  const renderRowStatus = (target: UpdateTarget) => {
    if (phase.kind === 'confirm') {
      return <span className="update-progress-row-status update-progress-row-status-pending">Pending</span>
    }
    if (phase.kind === 'running') {
      return (
        <span className="update-progress-row-status update-progress-row-status-running">
          <Loader2 className="h-3.5 w-3.5 animate-spin" />
          Updating…
        </span>
      )
    }
    if (phase.kind === 'error') {
      return (
        <span className="update-progress-row-status update-progress-row-status-failed">
          <AlertCircle className="h-3.5 w-3.5" />
          Aborted
        </span>
      )
    }
    const outcome = outcomesByKey.get(target.key)
    if (!outcome) {
      return <span className="update-progress-row-status update-progress-row-status-pending">No result</span>
    }
    if (outcome.scheduled) {
      return (
        <span className="update-progress-row-status update-progress-row-status-pending">
          <Clock className="h-3.5 w-3.5" />
          Scheduled
        </span>
      )
    }
    return outcome.success
      ? (
        <span className="update-progress-row-status update-progress-row-status-success">
          <CheckCircle2 className="h-3.5 w-3.5" />
          Updated
        </span>
      )
      : (
        <span
          className="update-progress-row-status update-progress-row-status-failed"
          title={outcome.error ?? undefined}
        >
          <XCircle className="h-3.5 w-3.5" />
          Failed
        </span>
      )
  }

  // ── footer changes per phase ───────────────────────────────────────────
  const renderFooter = () => {
    if (phase.kind === 'confirm') {
      return (
        <DialogFooter>
          <Button type="button" variant="outline" onClick={handleClose}>Cancel</Button>
          <Button type="button" variant="default" onClick={handleConfirm}>
            <RefreshCw className="h-3.5 w-3.5" />
            {isProject ? 'Update project' : 'Update now'}
          </Button>
        </DialogFooter>
      )
    }
    if (phase.kind === 'running') {
      return (
        <DialogFooter>
          <Button type="button" variant="outline" disabled>
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            Updating…
          </Button>
        </DialogFooter>
      )
    }
    return (
      <DialogFooter>
        <Button type="button" variant="default" onClick={handleClose}>Close</Button>
      </DialogFooter>
    )
  }

  // ── header + banner per phase ──────────────────────────────────────────
  const renderHeadline = () => {
    switch (phase.kind) {
      case 'confirm':
        return (
          <>
            {isProject ? 'Update project: ' : 'Update '}
            <code className="container-modal-code">{subject}</code>
          </>
        )
      case 'running':
        return (
          <>
            Updating <code className="container-modal-code">{subject}</code>…
          </>
        )
      case 'done': {
        if (phase.outcomes.some((o) => o.scheduled)) {
          return (
            <>
              Self-update scheduled: <code className="container-modal-code">{subject}</code>
            </>
          )
        }
        const failed = phase.outcomes.filter((o) => !o.success).length
        return (
          <>
            {failed === 0 ? 'Update complete: ' : 'Update finished with errors: '}
            <code className="container-modal-code">{subject}</code>
          </>
        )
      }
      case 'error':
        return (
          <>
            Update failed: <code className="container-modal-code">{subject}</code>
          </>
        )
    }
  }

  const renderBanner = () => {
    if (phase.kind === 'confirm') return description
    if (phase.kind === 'running') {
      return (
        <p className="update-progress-banner">
          <Loader2 className="h-4 w-4 animate-spin" />
          Pulling images and recreating
          {isProject ? ' containers… this can take a minute on a large project.' : ' the container…'}
        </p>
      )
    }
    if (phase.kind === 'error') {
      return (
        <p className="update-progress-banner update-progress-banner-failed">
          <AlertCircle className="h-4 w-4" /> {phase.message}
        </p>
      )
    }
    // done
    // V9.2 — self-update: the recreate runs in a detached helper and this
    // process is about to restart, so neither "updated" nor "failed" fits.
    const scheduled = phase.outcomes.find((o) => o.scheduled)
    if (scheduled) {
      return (
        <p className="update-progress-banner">
          <Clock className="h-4 w-4" />
          {scheduled.error ?? (
            <>Stashboard is updating itself in a detached helper container. The UI will be briefly
              unavailable while it restarts — refresh in a minute.</>
          )}
        </p>
      )
    }
    const okCount = phase.outcomes.filter((o) => o.success).length
    const total = phase.outcomes.length
    const failed = total - okCount
    const summary = buildSummary?.(phase.outcomes)
    if (failed === 0) {
      return (
        <p className="update-progress-banner update-progress-banner-success">
          <CheckCircle2 className="h-4 w-4" />
          {summary?.success ?? (
            isProject
              ? <>Project <code className="container-modal-code">{subject}</code> updated ({total} service{total === 1 ? '' : 's'}).</>
              : <>Container <code className="container-modal-code">{subject}</code> updated.</>
          )}
        </p>
      )
    }
    return (
      <p className="update-progress-banner update-progress-banner-failed">
        <AlertCircle className="h-4 w-4" />
        {summary?.failure ?? (
          isProject
            ? <>{failed} of {total} services failed — see each row for details.</>
            : <>Update failed — see the row below for details.</>
        )}
      </p>
    )
  }

  return (
    <Dialog open={open} onOpenChange={(v) => { if (!v) handleClose() }}>
      <DialogContent className="update-progress-dialog">
        <DialogHeader>
          <DialogTitle>{renderHeadline()}</DialogTitle>
          <DialogDescription className="sr-only">
            Confirm and run an update for {scope} {subject}
          </DialogDescription>
        </DialogHeader>

        <p className="update-progress-meta">
          {targets.length} {isProject ? `service${targets.length === 1 ? '' : 's'} · ` : `container · `}
          {trackedCount} tracked by a watch
        </p>

        {renderBanner()}

        <ul className="update-progress-list">
          {targets.map((target) => {
            const outcome = outcomesByKey.get(target.key)
            const isFailed = phase.kind === 'done' && outcome && !outcome.success
            const isSuccess = phase.kind === 'done' && outcome && outcome.success
            return (
              <li
                key={target.key}
                className="update-progress-row"
                data-state={
                  isSuccess ? 'success'
                    : isFailed ? 'failed'
                      : phase.kind === 'running' ? 'running'
                        : 'pending'
                }
                aria-disabled={phase.kind === 'done' || phase.kind === 'running'}
              >
                <div className="update-progress-row-meta">
                  <span className="update-progress-row-name">
                    {target.displayName}
                    {target.secondary && target.secondary !== target.displayName && (
                      <span className="update-progress-row-secondary"> ({target.secondary})</span>
                    )}
                  </span>
                  <code className="container-modal-code update-progress-row-image">{target.image}</code>
                  {!target.tracked && phase.kind === 'confirm' && (
                    <span className="update-progress-row-untracked">untracked — anonymous pull only</span>
                  )}
                  {isFailed && outcome?.error && (
                    <span className="update-progress-row-error">{outcome.error}</span>
                  )}
                </div>
                {renderRowStatus(target)}
              </li>
            )
          })}
        </ul>

        {phase.kind === 'confirm' && docs && (
          <p className="update-progress-docs">
            <a
              href={docs.href}
              target="_blank"
              rel="noopener noreferrer"
              className="docker-release-panel-link"
            >
              {docs.label}
              <ExternalLink className="h-3 w-3 inline ml-1" />
            </a>
          </p>
        )}

        {renderFooter()}
      </DialogContent>
    </Dialog>
  )
}
