import { AlertCircle, CheckCircle2, FileWarning } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import type { ComposeFileDiff } from '@/lib/types'

/**
 * V7.6 — the pre-save (and pre-restore) confirm dialog. Shows a unified line
 * diff of what's about to change, the `docker compose config -q` verdict, and
 * which services the change touches — so a save on a production project is never
 * blind. Purely presentational: the diff/validation come from the diff endpoint,
 * and the footer buttons (Save / Save & apply / Restore / …) are supplied by the
 * caller so the same dialog serves both the Raw-YAML editor and the History tab.
 */

export interface ComposeDiffDialogProps {
  open: boolean
  onClose: () => void
  title: string
  description?: string
  diff: ComposeFileDiff | null
  isLoading: boolean
  /** Error from computing the diff itself (not a validation failure). */
  error: string | null
  /** Footer action buttons, rendered right-aligned after a Cancel button. */
  actions: React.ReactNode
}

export function ComposeDiffDialog({
  open, onClose, title, description, diff, isLoading, error, actions,
}: ComposeDiffDialogProps) {
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose() }}>
      <DialogContent className="container-modal-content compose-diff-dialog">
        <DialogHeader>
          <DialogTitle className="container-modal-title">{title}</DialogTitle>
          {description && <DialogDescription>{description}</DialogDescription>}
        </DialogHeader>

        {isLoading && <p className="container-modal-empty">Computing the diff…</p>}
        {error && (
          <p className="container-modal-error">
            <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
          </p>
        )}

        {diff && !isLoading && (
          <div className="compose-diff-body">
            <ValidationBanner diff={diff} />

            {diff.unchanged ? (
              <p className="compose-diff-empty">No changes — the file already matches what's on disk.</p>
            ) : (
              <>
                <ServiceSummary diff={diff} />
                <DiffView diff={diff} />
              </>
            )}
          </div>
        )}

        <DialogFooter>
          <Button type="button" variant="outline" size="sm" className="mr-auto" onClick={onClose}>Cancel</Button>
          {actions}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function ValidationBanner({ diff }: { diff: ComposeFileDiff }) {
  if (!diff.cliAvailable) {
    return (
      <p className="compose-diff-validation compose-diff-validation--warn" role="status">
        <FileWarning className="h-3.5 w-3.5 inline" />{' '}
        No <code>docker compose</code> CLI is available to validate — the save will be refused.
      </p>
    )
  }
  if (diff.valid) {
    return (
      <p className="compose-diff-validation compose-diff-validation--ok" role="status">
        <CheckCircle2 className="h-3.5 w-3.5 inline" /> Validates with <code>docker compose config</code>.
      </p>
    )
  }
  return (
    <div className="compose-diff-validation compose-diff-validation--error" role="alert">
      <p>
        <AlertCircle className="h-3.5 w-3.5 inline" /> <strong>Invalid</strong> — <code>docker compose config</code> rejected it:
      </p>
      <pre className="compose-diff-validation-detail">{diff.validationError}</pre>
    </div>
  )
}

function ServiceSummary({ diff }: { diff: ComposeFileDiff }) {
  if (diff.changedServices.length === 0 && diff.removedServices.length === 0) return null
  return (
    <div className="compose-diff-services">
      {diff.changedServices.length > 0 && (
        <div className="compose-diff-services-row">
          <span className="compose-diff-services-label">Recreated by Apply:</span>
          {diff.changedServices.map((s) => (
            <code key={s} className="compose-diff-chip compose-diff-chip--changed">{s}</code>
          ))}
        </div>
      )}
      {diff.removedServices.length > 0 && (
        <div className="compose-diff-services-row">
          <span className="compose-diff-services-label">Removed from file:</span>
          {diff.removedServices.map((s) => (
            <code key={s} className="compose-diff-chip compose-diff-chip--removed">{s}</code>
          ))}
          <span className="compose-diff-services-hint">(their containers stay running — stop them manually)</span>
        </div>
      )}
    </div>
  )
}

function DiffView({ diff }: { diff: ComposeFileDiff }) {
  const gutter = (n: number | null) => (n === null ? '' : String(n))
  return (
    <div className="compose-diff-view font-mono text-[12px]" role="region" aria-label="Diff">
      {diff.diff.map((line, i) => {
        const sign = line.type === 'Added' ? '+' : line.type === 'Removed' ? '-' : ' '
        const cls =
          line.type === 'Added' ? 'compose-diff-line--add'
          : line.type === 'Removed' ? 'compose-diff-line--del'
          : ''
        return (
          <div key={i} className={`compose-diff-line ${cls}`}>
            <span className="compose-diff-ln" aria-hidden="true">{gutter(line.oldLine)}</span>
            <span className="compose-diff-ln" aria-hidden="true">{gutter(line.newLine)}</span>
            <span className="compose-diff-sign" aria-hidden="true">{sign}</span>
            <span className="compose-diff-text">{line.text || ' '}</span>
          </div>
        )
      })}
    </div>
  )
}
