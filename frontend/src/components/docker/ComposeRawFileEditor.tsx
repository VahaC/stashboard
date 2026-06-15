import { useCallback, useRef, useState } from 'react'
import { AlertCircle, Check, Copy, Download, RotateCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useComposeFile, useComposeUp, useSaveComposeFile } from '@/lib/queries'
import { getApiErrorMessage } from '@/lib/utils'

/**
 * V7.4 — the "Raw YAML" tab: the project's whole Compose file in a plain text
 * editor. Lets the user hand-write or paste a complete file — including several
 * services at once — when the structured forms don't fit. **Save** validates
 * the text with `docker compose config -q` and renames it over the original
 * atomically (the same writer the field editors use); **Save and run** then
 * brings the project up. Available for existing projects too, not just the
 * create flow.
 */

export interface ComposeRawFileEditorProps {
  connectionId: string
  project: string
}

function parseErrorLines(error: string | null): Set<number> {
  if (!error) return new Set()
  const nums = new Set<number>()
  for (const m of error.matchAll(/\bline (\d+)/g)) {
    nums.add(parseInt(m[1], 10))
  }
  return nums
}

export function ComposeRawFileEditor({ connectionId, project }: ComposeRawFileEditorProps) {
  const file = useComposeFile(connectionId, project, true)
  const saveFile = useSaveComposeFile(connectionId, project)
  const up = useComposeUp(connectionId, project)

  // `draft` is null until the user types — the textarea then shows the loaded
  // file text (no setState-in-effect needed to seed it).
  const [draft, setDraft] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const lineNumsRef = useRef<HTMLDivElement>(null)

  const syncScroll = useCallback(() => {
    if (lineNumsRef.current && textareaRef.current) {
      lineNumsRef.current.scrollTop = textareaRef.current.scrollTop
    }
  }, [])

  const content = draft ?? file.data?.content ?? ''
  const busy = saveFile.isPending || up.isPending
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
      submit(false)
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

  const submit = async (run: boolean) => {
    setError(null)
    setNotice(null)
    try {
      const result = await saveFile.mutateAsync(content)
      if (!run) {
        flash(result.changed ? 'Saved.' : 'No changes.')
        return
      }
      const upResult = await up.mutateAsync()
      if (upResult.success) {
        flash('Saved and started.')
        return
      }
      setError(`Saved, but "docker compose up -d" failed: ${upResult.error ?? 'unknown error'}`)
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to save the Compose file')
    }
  }

  return (
    <div className="compose-tab compose-raw-tab">
      <div className="compose-raw-head">
        <span className="compose-tab-name">
          {file.data ? `${file.data.fileName} · ${file.data.projectPath}` : 'Raw Compose file'}
          {dirty && <span className="compose-raw-dirty" title="Unsaved changes">*</span>}
        </span>
        <div className="compose-raw-head-actions">
          <Button
            type="button" variant="ghost" size="sm"
            onClick={reload}
            disabled={!file.data || busy || !dirty}
            title="Discard edits and reload from disk"
          >
            <RotateCcw className="h-3.5 w-3.5" />
            <span className="label-text">Reload</span>
          </Button>
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
            <Button type="button" size="sm" onClick={() => submit(true)} disabled={busy}>
              {up.isPending ? 'Starting…' : saveFile.isPending ? 'Saving…' : 'Save and run'}
            </Button>
            <Button
              type="button" variant="outline" size="sm"
              onClick={() => submit(false)}
              disabled={busy}
              title="Validate and write the file without starting anything"
            >
              Save only
            </Button>
          </div>
        </>
      )}
    </div>
  )
}
