import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Check, Copy, Download, Pause, Play, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { streamContainerLogs, type DockerLogsStreamHandle } from '@/lib/docker-logs'
import type { DockerLogLine } from '@/lib/types'

export type ContainerLogsPanelProps = {
  connectionId: string
  containerName: string
  /** When false, don't auto-start the stream on mount. Defaults to true. */
  autoStart?: boolean
}

const LOG_BUFFER_LIMIT = 5000

/**
 * Shared "Logs" panel — opens an NDJSON chunked tail against the
 * `(connectionId, containerName)` endpoint with the same controls the V3.5
 * modal already shipped (Pause / Resume / Stop / Stream / Clear / Download)
 * and the same stdout/stderr/timestamps toggles.
 *
 * Lifecycle: starts on mount when `autoStart`; stops on unmount or when the
 * `containerName` / `connectionId` changes. The buffer is bounded at
 * {@link LOG_BUFFER_LIMIT} lines so a long-running tail can't leak memory.
 */
export function ContainerLogsPanel({
  connectionId, containerName, autoStart = true,
}: ContainerLogsPanelProps) {
  const [lines, setLines] = useState<DockerLogLine[]>([])
  const [streaming, setStreaming] = useState(false)
  const [paused, setPaused] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showStdout, setShowStdout] = useState(true)
  const [showStderr, setShowStderr] = useState(true)
  const [showTimestamps, setShowTimestamps] = useState(true)
  const [copied, setCopied] = useState(false)

  const handleRef = useRef<DockerLogsStreamHandle | null>(null)
  const panelRef = useRef<HTMLDivElement | null>(null)
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const pausedRef = useRef(paused)
  pausedRef.current = paused

  const stopStream = useCallback(() => {
    handleRef.current?.abort()
    handleRef.current = null
    setStreaming(false)
  }, [])

  const startStream = useCallback(() => {
    stopStream()
    setError(null)
    setLines([])
    setStreaming(true)
    const handle = streamContainerLogs(
      connectionId,
      containerName,
      { follow: true, tail: 200, timestamps: true, stdout: true, stderr: true },
      (line) => {
        if (line.stream === 'error') { setError(line.message); return }
        setLines((prev) => prev.length >= LOG_BUFFER_LIMIT
          ? [...prev.slice(prev.length - LOG_BUFFER_LIMIT + 1), line]
          : [...prev, line])
      },
    )
    handleRef.current = handle
    handle.done.then(() => {
      if (handleRef.current === handle) { handleRef.current = null; setStreaming(false) }
    }).catch((err) => {
      setError(err instanceof Error ? err.message : String(err))
      if (handleRef.current === handle) { handleRef.current = null; setStreaming(false) }
    })
  }, [connectionId, containerName, stopStream])

  useEffect(() => {
    if (autoStart) startStream()
    return () => stopStream()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, containerName])

  useEffect(() => {
    if (pausedRef.current) return
    const el = viewportRef.current
    if (!el) return
    el.scrollTop = el.scrollHeight
  }, [lines])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'a') return
      if (event.altKey || event.shiftKey) return

      const activeEl = document.activeElement
      if (
        activeEl instanceof HTMLInputElement
        || activeEl instanceof HTMLTextAreaElement
        || activeEl instanceof HTMLButtonElement
        || activeEl instanceof HTMLSelectElement
        || activeEl?.getAttribute('contenteditable') === 'true'
      ) {
        return
      }

      const panelEl = panelRef.current
      const viewportEl = viewportRef.current
      const selection = window.getSelection()
      if (!panelEl || !viewportEl || !selection || selection.rangeCount === 0) return

      const anchorNode = selection.anchorNode
      const focusNode = selection.focusNode
      if (!anchorNode || !focusNode) return
      if (!panelEl.contains(anchorNode) || !panelEl.contains(focusNode)) return

      event.preventDefault()
      const range = document.createRange()
      range.selectNodeContents(viewportEl)
      selection.removeAllRanges()
      selection.addRange(range)
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  const downloadSnapshot = useCallback(async () => {
    const snapshot: DockerLogLine[] = []
    const handle = streamContainerLogs(
      connectionId, containerName,
      { follow: false, tail: 5000, timestamps: showTimestamps, stdout: true, stderr: true },
      (line) => snapshot.push(line),
    )
    try { await handle.done } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      return
    }
    const text = snapshot.map((l) => {
      const prefix = l.timestamp ? `${l.timestamp} ` : ''
      const channel = l.stream === 'stderr' ? '[stderr] ' : ''
      return `${prefix}${channel}${l.message}`
    }).join('\n') + '\n'
    const blob = new Blob([text], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${containerName}-${new Date().toISOString().replace(/[:.]/g, '-')}.log`
    document.body.appendChild(a); a.click(); document.body.removeChild(a)
    URL.revokeObjectURL(url)
  }, [connectionId, containerName, showTimestamps])

  const visible = lines.filter((l) =>
    (l.stream === 'stdout' && showStdout) || (l.stream === 'stderr' && showStderr))
  const statusLabel = error ? 'error' : streaming ? (paused ? 'paused' : 'streaming') : 'idle'

  const copyVisibleLogs = useCallback(async () => {
    const text = visible.map((line) => {
      const prefix = showTimestamps && line.timestamp ? `${line.timestamp} ` : ''
      const channel = line.stream === 'stderr' ? '[stderr] ' : ''
      return `${prefix}${channel}${line.message}`
    }).join('\n')

    if (typeof navigator === 'undefined' || !navigator.clipboard?.writeText) {
      setError('Clipboard API is not available in this browser context.')
      return
    }

    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 1600)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [showTimestamps, visible])

  return (
    <div ref={panelRef} className="container-modal-logs">
      <div className="docker-logs-toolbar">
        <span className="docker-logs-toolbar-status">
          <span className="docker-logs-toolbar-status-dot" data-state={statusLabel} /> {statusLabel}
        </span>
        <label className="docker-logs-toolbar-filter">
          <input type="checkbox" checked={showStdout} onChange={(e) => setShowStdout(e.target.checked)} /> stdout
        </label>
        <label className="docker-logs-toolbar-filter">
          <input type="checkbox" checked={showStderr} onChange={(e) => setShowStderr(e.target.checked)} /> stderr
        </label>
        <label className="docker-logs-toolbar-filter">
          <input type="checkbox" checked={showTimestamps} onChange={(e) => setShowTimestamps(e.target.checked)} /> timestamps
        </label>
        {streaming ? (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => setPaused((v) => !v)}>
              {paused ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
              {paused ? 'Resume' : 'Pause'}
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={stopStream}>
              <Square className="h-3.5 w-3.5" /> Stop
            </Button>
          </>
        ) : (
          <Button type="button" variant="outline" size="sm" onClick={startStream}>
            <Play className="h-3.5 w-3.5" /> Stream
          </Button>
        )}
        <Button type="button" variant="ghost" size="sm" onClick={() => setLines([])}>Clear</Button>
        <Button type="button" variant="ghost" size="sm" onClick={() => void copyVisibleLogs()}>
          {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />} {copied ? 'Copied' : 'Copy'}
        </Button>
        <Button type="button" variant="ghost" size="sm" onClick={() => void downloadSnapshot()}>
          <Download className="h-3.5 w-3.5" /> Download
        </Button>
      </div>
      {error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
      <div ref={viewportRef} className="docker-logs-viewport">
        {visible.length === 0 ? (
          <div className="docker-logs-viewport-empty">
            {streaming ? 'Waiting for log output…' : 'No log lines.'}
          </div>
        ) : (
          visible.map((line, idx) => (
            <div key={idx} className="docker-logs-line" data-stream={line.stream}>
              {showTimestamps && line.timestamp && (
                <span className="docker-logs-line-time">{formatLogTime(line.timestamp)}</span>
              )}
              <span className="docker-logs-line-message">{line.message}</span>
            </div>
          ))
        )}
      </div>
    </div>
  )
}

const formatLogTime = (iso: string): string => {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  const pad = (n: number, w = 2) => n.toString().padStart(w, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
}
