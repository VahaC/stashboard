import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Check, Copy, Download, Pause, Play, ScrollText, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { openProxmoxLogs, type ProxmoxLogsHandle } from '@/lib/proxmox-logs'
// Reuse the Docker logs panel styles (toolbar + viewport) and the host-terminal
// disabled-state styles verbatim so the LXC logs tab is the same surface, not a
// parallel one.
import '@/styles/docker-instances.css'

const LOG_BUFFER_LIMIT = 5000

export interface LxcLogsPanelProps {
  connectionId: string
  vmId: number
  /** Guest runtime state — `pct exec` needs a running LXC. */
  isRunning: boolean
  /** Whether the host has SSH credentials configured (the tail SSHes in). */
  sshConfigured: boolean
  /** Per-host opt-in (ProxmoxConnection.allowConsole) — shared with the console. */
  allowConsole: boolean
  /** Global master switch (StashboardFeatures.allowProxmoxConsole). */
  allowConsoleGlobal: boolean
  /** Whether the Logs tab is the active tab. The panel is kept mounted across tab
   *  switches (so the stream survives); when it becomes active again we re-snap the
   *  viewport to the bottom. Defaults to true for standalone use. */
  active?: boolean
}

/**
 * V6.12 — the LXC Logs tab: a read-only live tail of the guest's system journal.
 * It is the LXC analogue of the Docker {@link
 * file://../docker/ContainerLogsPanel.tsx} — same toolbar (Pause / Resume / Stop /
 * Stream / Clear / Copy / Download) and the same autoscrolling viewport — but the
 * stream rides the V6.6 console SSH/WebSocket transport instead of the Docker
 * NDJSON one, and there are no stdout/stderr filters (a PTY merges the streams and
 * journalctl prints its own ISO timestamps inline).
 *
 * Gated identically to the Console (global switch + per-host opt-in + SSH +
 * running guest); the tab is always present so the affordance is discoverable, and
 * each gate shows the same calm hint as the rest of the modal.
 */
export function LxcLogsPanel({
  connectionId, vmId, isRunning, sshConfigured, allowConsole, allowConsoleGlobal, active = true,
}: LxcLogsPanelProps) {
  if (!allowConsoleGlobal) {
    return (
      <DisabledState
        title="LXC logs are disabled on this server"
        hint="An operator must enable the guest console at Settings → Guest console. The live log tail uses the same SSH channel, so it shares that switch."
      />
    )
  }
  if (!allowConsole) {
    return (
      <DisabledState
        title="LXC logs are not enabled for this host"
        hint="Turn on 'Allow guest console' in this Proxmox host's settings (Edit host) to tail its containers' journals."
      />
    )
  }
  if (!sshConfigured) {
    return (
      <DisabledState
        title="This host has no SSH credentials"
        hint="The log tail SSHes to the Proxmox host and runs 'journalctl -f' inside the LXC. Add SSH host / username / private key on the Proxmox host (Edit) first."
      />
    )
  }
  if (!isRunning) {
    return (
      <DisabledState
        title="Container is not running"
        hint="The journal tail reads from a live container — start the LXC first, then reopen this tab."
      />
    )
  }
  return <LiveLogs connectionId={connectionId} vmId={vmId} active={active} />
}

function DisabledState({ title, hint }: { title: string; hint: string }) {
  return (
    <div className="host-terminal-disabled">
      <ScrollText className="h-6 w-6 host-terminal-disabled-icon" />
      <p className="host-terminal-disabled-title">{title}</p>
      <p className="host-terminal-disabled-hint">{hint}</p>
    </div>
  )
}

function LiveLogs({ connectionId, vmId, active }: { connectionId: string; vmId: number; active: boolean }) {
  const [lines, setLines] = useState<string[]>([])
  const [streaming, setStreaming] = useState(false)
  const [paused, setPaused] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const handleRef = useRef<ProxmoxLogsHandle | null>(null)
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const pausedRef = useRef(paused)
  useEffect(() => { pausedRef.current = paused }, [paused])
  // Monotonic run id: each start/stop bumps it. The ticket→socket open is async,
  // so a handle (or a late callback) can arrive after its run was superseded — by
  // a Stop, an unmount, or React StrictMode's mount→unmount→remount. When the
  // resolved run is no longer current we *close* the handle instead of storing it,
  // so a stale in-flight socket can never leak a live SSH session (which would
  // otherwise pile up against the per-user session cap).
  const runIdRef = useRef(0)

  const stopStream = useCallback(() => {
    runIdRef.current += 1
    handleRef.current?.close()
    handleRef.current = null
    setStreaming(false)
  }, [])

  const startStream = useCallback(() => {
    handleRef.current?.close()
    handleRef.current = null
    const myRun = (runIdRef.current += 1)
    setError(null)
    setLines([])
    setStreaming(true)
    void openProxmoxLogs(connectionId, vmId, {
      follow: true,
      onLine: (line) => {
        if (runIdRef.current !== myRun) return
        setLines((prev) => prev.length >= LOG_BUFFER_LIMIT
          ? [...prev.slice(prev.length - LOG_BUFFER_LIMIT + 1), line]
          : [...prev, line])
      },
      onClose: (reason) => {
        if (runIdRef.current !== myRun) return
        handleRef.current = null
        setStreaming(false)
        // A server-initiated close — e.g. the per-user/-host session cap, or the
        // tail ending — surfaced so the panel doesn't drop silently to "idle".
        if (reason) setError(reason)
      },
      onError: (message) => {
        if (runIdRef.current !== myRun) return
        setError(message)
        handleRef.current = null
        setStreaming(false)
      },
    }).then((handle) => {
      // Superseded while the ticket POST was in flight → close, never store.
      if (runIdRef.current !== myRun) { handle.close(); return }
      handleRef.current = handle
    }).catch(() => { /* onError already surfaced it */ })
  }, [connectionId, vmId])

  useEffect(() => {
    // Subscribing to an external log stream; setState here is intentional.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    startStream()
    return () => stopStream()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, vmId])

  useEffect(() => {
    if (pausedRef.current) return
    const el = viewportRef.current
    if (!el) return
    el.scrollTop = el.scrollHeight
  }, [lines])

  // When the tab is re-shown after being hidden (kept mounted across switches),
  // snap back to the bottom so the user sees the latest output, unless paused.
  useEffect(() => {
    if (!active || pausedRef.current) return
    const el = viewportRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [active])

  const resume = useCallback(() => {
    setPaused(false)
    const el = viewportRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [])

  const downloadSnapshot = useCallback(async () => {
    const snapshot: string[] = []
    let resolveDone!: () => void
    let rejectDone!: (e: unknown) => void
    const done = new Promise<void>((res, rej) => { resolveDone = res; rejectDone = rej })

    let handle: ProxmoxLogsHandle
    try {
      handle = await openProxmoxLogs(connectionId, vmId, {
        follow: false,
        onLine: (line) => snapshot.push(line),
        onClose: () => resolveDone(),
        onError: (message) => rejectDone(new Error(message)),
      })
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      return
    }

    try {
      await done
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      return
    } finally {
      handle.close()
    }

    const text = snapshot.join('\n') + '\n'
    const blob = new Blob([text], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `lxc-${vmId}-${new Date().toISOString().replace(/[:.]/g, '-')}.log`
    document.body.appendChild(a); a.click(); document.body.removeChild(a)
    URL.revokeObjectURL(url)
  }, [connectionId, vmId])

  const copyVisibleLogs = useCallback(async () => {
    const text = lines.join('\n')
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
  }, [lines])

  const statusLabel = error ? 'error' : streaming ? (paused ? 'paused' : 'streaming') : 'idle'

  return (
    <div className="container-modal-logs">
      <div className="docker-logs-toolbar">
        <span className="docker-logs-toolbar-status">
          <span className="docker-logs-toolbar-status-dot" data-state={statusLabel} /> {statusLabel}
        </span>
        {streaming ? (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => (paused ? resume() : setPaused(true))}>
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
        {lines.length === 0 ? (
          <div className="docker-logs-viewport-empty">
            {streaming ? 'Waiting for journal output…' : 'No log lines.'}
          </div>
        ) : (
          lines.map((line, idx) => (
            <div key={idx} className="docker-logs-line">
              <span className="docker-logs-line-message">{line}</span>
            </div>
          ))
        )}
      </div>
    </div>
  )
}
