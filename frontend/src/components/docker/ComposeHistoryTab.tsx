import { useState } from 'react'
import { AlertCircle, History, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  fetchComposeHistoryFile,
  useApplyComposeServices,
  useComposeFileDiff,
  useComposeHistory,
  useRestoreComposeHistory,
} from '@/lib/queries'
import { formatBytes, formatTimestamp } from '@/lib/audit-api'
import { getApiErrorMessage } from '@/lib/utils'
import { ComposeDiffDialog } from './ComposeDiffDialog'

/**
 * V7.6 — the "History" tab: the last N revisions of the project's Compose file
 * kept under `<project>/.stashboard/history/`. Each row can be **Restored**,
 * which first previews exactly what would change (the same diff + dry-run dialog
 * the Raw editor uses, here comparing the revision against the current file) and
 * then, on confirm, re-validates and writes it back — snapshotting the current
 * file first, so a restore is itself undoable.
 */

export interface ComposeHistoryTabProps {
  connectionId: string
  project: string
}

export function ComposeHistoryTab({ connectionId, project }: ComposeHistoryTabProps) {
  const history = useComposeHistory(connectionId, project, true)
  const diff = useComposeFileDiff(connectionId, project)
  const restore = useRestoreComposeHistory(connectionId, project)
  const apply = useApplyComposeServices(connectionId, project)

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [previewBusy, setPreviewBusy] = useState(false)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const busy = restore.isPending || apply.isPending

  const flash = (msg: string) => {
    setNotice(msg)
    setTimeout(() => setNotice(null), 2500)
  }

  const beginRestore = async (id: string) => {
    setSelectedId(id)
    setConfirmOpen(true)
    setPreviewError(null)
    setError(null)
    setPreviewBusy(true)
    diff.reset()
    try {
      const revision = await fetchComposeHistoryFile(connectionId, project, id)
      diff.mutate(revision.content)
    } catch (e: unknown) {
      setPreviewError(getApiErrorMessage(e) ?? 'Failed to load the revision')
    } finally {
      setPreviewBusy(false)
    }
  }

  const doRestore = async (applyChanged: boolean) => {
    if (!selectedId) return
    try {
      await restore.mutateAsync(selectedId)
      if (applyChanged) {
        const services = diff.data?.changedServices ?? []
        const applied = await apply.mutateAsync(services)
        setConfirmOpen(false)
        if (!applied.success) {
          setError(`Restored, but apply failed: ${applied.error ?? 'unknown error'}`)
          return
        }
        flash('Restored and applied.')
        return
      }
      setConfirmOpen(false)
      flash('Restored.')
    } catch (e: unknown) {
      setConfirmOpen(false)
      setError(getApiErrorMessage(e) ?? 'Failed to restore the revision')
    }
  }

  const diffData = diff.data ?? null
  const isLoading = previewBusy || diff.isPending
  const canRestore = diffData != null && diffData.valid && !busy
  const canApply = canRestore && !diffData.unchanged && diffData.changedServices.length > 0

  return (
    <div className="compose-tab compose-history-tab">
      <div className="compose-raw-head">
        <span className="compose-tab-name">
          <History className="h-3.5 w-3.5 inline" /> Revision history
        </span>
        {notice && <span className="compose-raw-toast" role="status">{notice}</span>}
      </div>

      {history.isLoading && <p className="container-modal-empty">Loading history…</p>}
      {history.error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
          {getApiErrorMessage(history.error) ?? 'Failed to load the history'}
        </p>
      )}
      {history.data && history.data.length === 0 && (
        <p className="container-modal-empty">
          No revisions yet. They're saved automatically each time the Compose file changes.
        </p>
      )}

      {error && <pre className="compose-edit-error" role="alert">{error}</pre>}

      {history.data && history.data.length > 0 && (
        <ul className="compose-history-list">
          {history.data.map((entry) => (
            <li key={entry.id} className="compose-history-row">
              <div className="compose-history-meta">
                <span className="compose-history-when">{formatTimestamp(entry.savedUtc)}</span>
                <span className="compose-history-size">{formatBytes(entry.sizeBytes)}</span>
              </div>
              <Button
                type="button" variant="outline" size="sm"
                onClick={() => beginRestore(entry.id)}
                disabled={busy}
                title="Preview and restore this revision"
              >
                <RotateCcw className="h-3.5 w-3.5" />
                <span className="label-text">Restore</span>
              </Button>
            </li>
          ))}
        </ul>
      )}

      <ComposeDiffDialog
        open={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title="Restore revision"
        description="What restoring this revision would change vs. the file on disk."
        diff={diffData}
        isLoading={isLoading}
        error={previewError ?? (diff.isError ? (getApiErrorMessage(diff.error) ?? 'Failed to compute the diff') : null)}
        actions={
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => doRestore(false)} disabled={!canRestore}>
              {restore.isPending && !apply.isPending ? 'Restoring…' : 'Restore'}
            </Button>
            <Button
              type="button" size="sm" onClick={() => doRestore(true)} disabled={!canApply}
              title={canApply ? 'Restore, then recreate the changed services' : 'No changed services to recreate'}
            >
              {apply.isPending ? 'Applying…' : 'Restore & apply changed'}
            </Button>
          </>
        }
      />
    </div>
  )
}
