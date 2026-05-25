import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Plug, PlugZap, TerminalSquare } from 'lucide-react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { Button } from '@/components/ui/button'
import { openHostShell, type HostShellHandle } from '@/lib/host-shell'

type Status = 'idle' | 'connecting' | 'connected' | 'closed' | 'error'

export interface HostTerminalPanelProps {
  connectionId: string
  /** Resolved host type for the connection ('LocalSocket' | 'TcpTls' | 'Ssh'). */
  hostType: 'LocalSocket' | 'TcpTls' | 'Ssh'
  /** Per-connection opt-in (DockerConnection.allowHostShell). */
  allowHostShell: boolean
  /** Global master switch (StashboardFeatures.allowHostShell). */
  allowHostShellGlobal: boolean
}

/**
 * V5.3 — host terminal tab. The tab is always present on the container modal so
 * the affordance is discoverable, but the live xterm.js terminal only renders
 * for SSH connections that have opted in (and when the server's global flag is
 * on). Every other case shows a disabled explainer.
 */
export function HostTerminalPanel({
  connectionId, hostType, allowHostShell, allowHostShellGlobal,
}: HostTerminalPanelProps) {
  if (hostType !== 'Ssh') {
    return (
      <DisabledState
        title="Available only for SSH tunnel connections"
        hint="The host terminal opens an interactive shell on the Docker host over SSH. Local-socket and TCP+TLS connections don't expose a host shell — change this connection's host type to SSH to use it."
      />
    )
  }
  if (!allowHostShellGlobal) {
    return (
      <DisabledState
        title="Host terminal is disabled on this server"
        hint="An operator must enable it by setting Stashboard:AllowHostShell=true (env: STASHBOARD_Stashboard__AllowHostShell=true) and restarting Stashboard."
      />
    )
  }
  if (!allowHostShell) {
    return (
      <DisabledState
        title="Host terminal is not enabled for this connection"
        hint="Turn on 'Allow host terminal' in this connection's settings (Edit connection) to open a shell on the host."
      />
    )
  }
  return <LiveTerminal connectionId={connectionId} />
}

function DisabledState({ title, hint }: { title: string; hint: string }) {
  return (
    <div className="host-terminal-disabled">
      <TerminalSquare className="h-6 w-6 host-terminal-disabled-icon" />
      <p className="host-terminal-disabled-title">{title}</p>
      <p className="host-terminal-disabled-hint">{hint}</p>
    </div>
  )
}

function LiveTerminal({ connectionId }: { connectionId: string }) {
  const [status, setStatus] = useState<Status>('idle')
  const [error, setError] = useState<string | null>(null)

  const containerRef = useRef<HTMLDivElement | null>(null)
  const termRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const handleRef = useRef<HostShellHandle | null>(null)
  const resizeObserverRef = useRef<ResizeObserver | null>(null)
  const clipAbortRef = useRef<AbortController | null>(null)
  // Guards the async ticket→socket window so a disconnect mid-connect doesn't
  // leave a live socket behind.
  const cancelledRef = useRef(false)

  const teardown = useCallback(() => {
    cancelledRef.current = true
    resizeObserverRef.current?.disconnect()
    resizeObserverRef.current = null
    clipAbortRef.current?.abort()
    clipAbortRef.current = null
    handleRef.current?.close()
    handleRef.current = null
    termRef.current?.dispose()
    termRef.current = null
    fitRef.current = null
  }, [])

  const disconnect = useCallback(() => {
    teardown()
    setStatus('closed')
  }, [teardown])

  const connect = useCallback(async () => {
    if (!containerRef.current) return
    cancelledRef.current = false
    setError(null)
    setStatus('connecting')

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: 'Consolas, "DejaVu Sans Mono", "Lucida Console", ui-monospace, monospace',
      fontSize: 13,
      scrollback: 5000,
      theme: { background: '#0b1020', foreground: '#e2e8f0', cursor: '#e2e8f0' },
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.open(containerRef.current)
    try { fit.fit() } catch { /* container not laid out yet */ }
    termRef.current = term
    fitRef.current = fit

    // Clipboard: Ctrl+Shift+C → copy selection, Ctrl+Shift+V → paste
    term.attachCustomKeyEventHandler((e: KeyboardEvent) => {
      if (e.type !== 'keydown') return true
      if (e.ctrlKey && e.shiftKey && e.code === 'KeyC') {
        const sel = term.getSelection()
        if (sel) void navigator.clipboard.writeText(sel)
        return false
      }
      if (e.ctrlKey && e.shiftKey && e.code === 'KeyV') {
        void navigator.clipboard.readText().then((text) => {
          if (text) handleRef.current?.send(text)
        })
        return false
      }
      return true
    })
    // Right-click → paste from clipboard
    const clipAc = new AbortController()
    clipAbortRef.current = clipAc
    containerRef.current.addEventListener('contextmenu', (e) => {
      e.preventDefault()
      const sel = term.getSelection()
      if (sel) {
        void navigator.clipboard.writeText(sel)
      } else {
        void navigator.clipboard.readText().then((text) => {
          if (text) handleRef.current?.send(text)
        })
      }
    }, { signal: clipAc.signal })

    let handle: HostShellHandle
    try {
      handle = await openHostShell(connectionId, {
        cols: term.cols,
        rows: term.rows,
        onData: (chunk) => term.write(chunk),
        onOpen: () => {
          if (cancelledRef.current) return
          setStatus('connected')
          term.focus()
        },
        onClose: (reason) => {
          if (cancelledRef.current) return
          setStatus('closed')
          term.writeln(`\r\n\x1b[90m[ ${reason || 'session ended'} ]\x1b[0m`)
        },
        onError: (message) => {
          if (cancelledRef.current) return
          setError(message)
          setStatus('error')
        },
      })
    } catch (err) {
      // openHostShell already surfaced the message via onError when it could;
      // ensure the terminal is cleaned up either way.
      setError(err instanceof Error ? err.message : 'Failed to start host terminal.')
      setStatus('error')
      term.dispose()
      termRef.current = null
      return
    }

    if (cancelledRef.current) {
      handle.close()
      return
    }
    handleRef.current = handle

    // Forward keystrokes / paste to the host.
    term.onData((data) => handleRef.current?.send(data))

    // Keep the PTY size in sync with the panel (best-effort — live resize may be
    // a no-op server-side, but the initial size is always honoured).
    const observer = new ResizeObserver(() => {
      try {
        fit.fit()
        handleRef.current?.resize(term.cols, term.rows)
      } catch { /* mid-teardown */ }
    })
    observer.observe(containerRef.current)
    resizeObserverRef.current = observer
  }, [connectionId])

  // Tear everything down on unmount (modal closed / tab switched away).
  useEffect(() => () => teardown(), [teardown])

  const connecting = status === 'connecting'
  const connected = status === 'connected'

  return (
    <div className="host-terminal-panel">
      <div className="host-terminal-toolbar">
        <span className="host-terminal-status" data-state={status}>
          <span className="host-terminal-status-dot" data-state={status} />
          {statusLabel(status)}
        </span>
        {connected ? (
          <Button type="button" variant="outline" size="sm" onClick={disconnect}>
            <PlugZap className="h-3.5 w-3.5" /> Disconnect
          </Button>
        ) : (
          <Button type="button" size="sm" onClick={() => void connect()} disabled={connecting}>
            <Plug className="h-3.5 w-3.5" /> {connecting ? 'Connecting…' : status === 'idle' ? 'Connect' : 'Reconnect'}
          </Button>
        )}
        <span className="host-terminal-warning">
          You are about to open a shell on the Docker host. Every session is audited.
        </span>
      </div>
      {error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
      <div ref={containerRef} className="host-terminal-viewport" data-active={status === 'idle' ? 'false' : 'true'} />
    </div>
  )
}

function statusLabel(status: Status): string {
  switch (status) {
    case 'connecting': return 'connecting'
    case 'connected': return 'connected'
    case 'closed': return 'closed'
    case 'error': return 'error'
    default: return 'idle'
  }
}
