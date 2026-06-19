import { useCallback, useRef, useState } from 'react'
import { AlertCircle, Check, Copy, Download, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  useApplyComposeServices,
  useComposeFile,
  useComposeFileDiff,
  useSaveComposeFile,
} from '@/lib/queries'
import { getApiErrorMessage } from '@/lib/utils'
import { ComposeDiffDialog } from './ComposeDiffDialog'

/**
 * V7.4 — the "Raw YAML" tab: the project's whole Compose file in a plain text
 * editor. V7.6 — **Save** no longer writes blindly: it opens a diff + dry-run
 * confirm dialog (see what changes, see the `docker compose config -q` verdict,
 * see which services are touched) and only writes on confirm — atomically, the
 * same writer the field editors use. From the dialog the user can also **Apply**
 * the change immediately, recreating just the changed services. Available for
 * existing projects too, not just the create flow.
 */

export interface ComposeRawFileEditorProps {
  connectionId: string
  project: string
}

export function ComposeRawFileEditor({ connectionId, project }: ComposeRawFileEditorProps) {
  const file = useComposeFile(connectionId, project, true)
  const saveFile = useSaveComposeFile(connectionId, project)
  const diff = useComposeFileDiff(connectionId, project)
  const apply = useApplyComposeServices(connectionId, project)

  // `draft` is null until the user types — the textarea then shows the loaded
  // file text (no setState-in-effect needed to seed it).
  const [draft, setDraft] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [confirmOpen, setConfirmOpen] = useState(false)

  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const lineNumsRef = useRef<HTMLDivElement>(null)

  const syncScroll = useCallback(() => {
    if (lineNumsRef.current && textareaRef.current) {
      lineNumsRef.current.scrollTop = textareaRef.current.scrollTop
    }
  }, [])

  const content = draft ?? file.data?.content ?? ''
  const busy = saveFile.isPending || apply.isPending
  const dirty = file.data != null && draft != null && draft !== file.data.content

  const lineCount = Math.max(content.split('\n').length, 1)
  const errorLines = parseErrorLines(error)
  const gutterWidth = `calc(${String(lineCount).length}ch + 1.5rem)`

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Tab') {
      e.preventDefault()
      const ta = e.currentTarget
      const start = ta.selectionStart
      const end = ta.selectionEnd
      const next = content.slice(0, start) + '  ' + content.slice(end)
      setDraft(next)
      setNotice(null)
      requestAnimationFrame(() => { ta.selectionStart = ta.selectionEnd = start + 2 })
    }
    if ((e.ctrlKey || e.metaKey) && e.key === 's') {
      e.preventDefault()
      openReview()
    }
  }

  const flash = (msg: string) => {
    setNotice(msg)
    setTimeout(() => setNotice(null), 2500)
  }

  const reload = () => {
    setDraft(null)
    setError(null)
    setNotice(null)
  }

  const copyToClipboard = async () => {
    await navigator.clipboard.writeText(content)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  const downloadFile = () => {
    const fileName = file.data?.fileName ?? 'docker-compose.yml'
    const blob = new Blob([content], { type: 'text/yaml' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    a.click()
    URL.revokeObjectURL(url)
  }

  // V7.6 — open the confirm dialog and compute the diff for `content`.
  const openReview = () => {
    setError(null)
    setNotice(null)
    setConfirmOpen(true)
    diff.mutate(content)
  }

  // V7.6 — write the file (validated atomically), then optionally recreate the
  // changed services.
  const save = async (applyChanged: boolean) => {
    try {
      const result = await saveFile.mutateAsync(content)
      setDraft(null) // re-sync the editor to the freshly saved file
      if (!applyChanged) {
        setConfirmOpen(false)
        flash(result.changed ? 'Saved.' : 'No changes.')
        return
      }
      const services = diff.data?.changedServices ?? []
      const applied = await apply.mutateAsync(services)
      setConfirmOpen(false)
      if (applied.success) {
        flash(services.length > 0 ? `Saved and applied ${services.length} service(s).` : 'Saved and applied.')
        return
      }
      setError(`Saved, but apply failed: ${applied.error ?? 'unknown error'}`)
    } catch (e: unknown) {
      setConfirmOpen(false)
      setError(getApiErrorMessage(e) ?? 'Failed to save the Compose file')
    }
  }

  const diffData = diff.data ?? null
  const canSave = diffData != null && diffData.valid && !busy
  const canApply = canSave && !diffData.unchanged && diffData.changedServices.length > 0

  return (
    <div className="compose-tab compose-raw-tab">
      <div className="compose-raw-head">
        <span className="compose-tab-name">
          {file.data ? `${file.data.fileName} · ${file.data.projectPath}` : 'Raw Compose file'}
          {dirty && <span className="compose-raw-dirty" title="Unsaved changes">*</span>}
        </span>
        <div className="compose-raw-head-actions">
          <Button
            type="button" variant="outline" size="icon"
            className="compose-raw-icon-btn"
            onClick={copyToClipboard}
            disabled={!file.data}
            title="Copy to clipboard"
          >
            {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
          </Button>
          <Button
            type="button" variant="outline" size="icon"
            className="compose-raw-icon-btn"
            onClick={downloadFile}
            disabled={!file.data}
            title="Download file"
          >
            <Download className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {file.isLoading && <p className="container-modal-empty">Loading the Compose file…</p>}
      {file.error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
          {getApiErrorMessage(file.error) ?? 'Failed to load the Compose file'}
        </p>
      )}

      {file.data && (
        <>
          <div className="compose-raw-editor">
            <div
              ref={lineNumsRef}
              className="compose-raw-linenos font-mono text-[12px]"
              style={{ width: gutterWidth }}
              aria-hidden="true"
            >
              {Array.from({ length: lineCount }, (_, i) => (
                <div
                  key={i + 1}
                  className={`compose-raw-linenum${errorLines.has(i + 1) ? ' compose-raw-linenum--error' : ''}`}
                >
                  {i + 1}
                </div>
              ))}
            </div>
            <textarea
              ref={textareaRef}
              className="compose-raw-textarea font-mono text-[12px]"
              value={content}
              onChange={(e) => { setDraft(e.target.value); setNotice(null) }}
              onKeyDown={handleKeyDown}
              onScroll={syncScroll}
              spellCheck={false}
              aria-label="Raw Compose file"
            />
          </div>

          {error && <pre className="compose-edit-error" role="alert">{error}</pre>}

          <div className="compose-edit-actions">
            {notice && <span className="compose-raw-toast" role="status">{notice}</span>}
            <Button
              type="button" variant="outline" size="sm"
              onClick={reload}
              disabled={!dirty || busy}
              title="Discard edits and reload the file from disk"
            >
              <RotateCcw className="h-3.5 w-3.5" />
              <span className="label-text">Discard</span>
            </Button>
            <Button type="button" size="sm" onClick={openReview} disabled={busy}>
              Review &amp; save…
            </Button>
          </div>

          <ComposeDiffDialog
            open={confirmOpen}
            onClose={() => setConfirmOpen(false)}
            title="Review changes"
            description={`${file.data.fileName} · ${file.data.projectPath}`}
            diff={diffData}
            isLoading={diff.isPending}
            error={diff.isError ? (getApiErrorMessage(diff.error) ?? 'Failed to compute the diff') : null}
            actions={
              <>
                <Button type="button" variant="outline" size="sm" onClick={() => save(false)} disabled={!canSave}>
                  {saveFile.isPending && !apply.isPending ? 'Saving…' : 'Save only'}
                </Button>
                <Button
                  type="button" size="sm" onClick={() => save(true)} disabled={!canApply}
                  title={canApply
                    ? 'Save, then recreate the changed services'
                    : 'No changed services to recreate'}
                >
                  {apply.isPending ? 'Applying…' : 'Save & apply changed'}
                </Button>
              </>
            }
          />
        </>
      )}
    </div>
  )
}

function parseErrorLines(error: string | null): Set<number> {
  if (!error) return new Set()
  const nums = new Set<number>()
  for (const m of error.matchAll(/\bline (\d+)/g)) {
    nums.add(parseInt(m[1], 10))
  }
  return nums
}
