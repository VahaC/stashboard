import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertCircle, Plug, PlugZap, ScrollText, TerminalSquare } from 'lucide-react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { Button } from '@/components/ui/button'
import { openContainerExec, type ContainerExecHandle } from '@/lib/container-exec'

type Status = 'idle' | 'connecting' | 'connected' | 'closed' | 'error'

export interface ContainerExecPanelProps {
  connectionId: string
  containerName: string
  /** Container runtime state — exec needs a running container. */
  state: string
  /** Per-connection opt-in (DockerConnection.allowExec). */
  allowExec: boolean
  /** Global master switch (StashboardFeatures.allowContainerExec). */
  allowExecGlobal: boolean
}

/**
 * V5.7 — container-exec tab. The tab is always present on the container modal so
 * the affordance is discoverable, but the live xterm.js terminal only renders
 * when the server's global switch is on AND this connection has opted in. Unlike
 * the V5.3 host terminal, exec works for every host type (it goes through the
 * Docker daemon, not SSH), so there is no host-type gate here.
 */
export function ContainerExecPanel({
  connectionId, containerName, state, allowExec, allowExecGlobal,
}: ContainerExecPanelProps) {
  if (!allowExecGlobal) {
    return (
      <DisabledState
        title="Container exec is disabled on this server"
        hint="An operator must enable it at Settings → Container exec. It's off by default because exec runs arbitrary commands inside the container."
      />
    )
  }
  if (!allowExec) {
    return (
      <DisabledState
        title="Container exec is not enabled for this connection"
        hint="Turn on 'Allow container exec' in this connection's settings (Edit connection) to open a shell inside its containers."
      />
    )
  }
  if (state.toLowerCase() !== 'running') {
    return (
      <DisabledState
        title="Container is not running"
        hint="Exec attaches a shell to a live process — start the container first, then reopen this tab."
      />
    )
  }
  return <LiveTerminal connectionId={connectionId} containerName={containerName} />
}

function SessionHistoryLink({ connectionId }: { connectionId: string }) {
  return (
    <p className="mt-2 text-xs">
      <Link
        to={`/audit?tab=exec&connectionId=${connectionId}`}
        className="text-[var(--primary)] underline inline-flex items-center gap-1"
      >
        <ScrollText className="h-3.5 w-3.5" /> View session history
      </Link>
    </p>
  )
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

function LiveTerminal({ connectionId, containerName }: { connectionId: string; containerName: string }) {
  const [status, setStatus] = useState<Status>('idle')
  const [error, setError] = useState<string | null>(null)
  const [command, setCommand] = useState('/bin/sh')

  const containerRef = useRef<HTMLDivElement | null>(null)
  const termRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const handleRef = useRef<ContainerExecHandle | null>(null)
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
    // Right-click → copy selection or paste from clipboard
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

    let handle: ContainerExecHandle
    try {
      handle = await openContainerExec(connectionId, containerName, {
        cols: term.cols,
        rows: term.rows,
        command: command.trim() || undefined,
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
      // openContainerExec already surfaced the message via onError when it
      // could; ensure the terminal is cleaned up either way.
      setError(err instanceof Error ? err.message : 'Failed to start container exec.')
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

    // Forward keystrokes / paste to the container.
    term.onData((data) => handleRef.current?.send(data))

    // Keep the PTY size in sync with the panel — live resize works for exec.
    const observer = new ResizeObserver(() => {
      try {
        fit.fit()
        handleRef.current?.resize(term.cols, term.rows)
      } catch { /* mid-teardown */ }
    })
    observer.observe(containerRef.current)
    resizeObserverRef.current = observer
  }, [connectionId, containerName, command])

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
        <label className="container-exec-command">
          <span className="container-exec-command-label">Command</span>
          <input
            type="text"
            value={command}
            disabled={connected || connecting}
            spellCheck={false}
            autoCapitalize="off"
            autoCorrect="off"
            className="container-exec-command-input"
            onChange={(e) => setCommand(e.target.value)}
            placeholder="/bin/sh"
          />
        </label>
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
          You are about to open a shell inside this container. Every session is audited.
        </span>
      </div>
      {error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
      <div ref={containerRef} className="host-terminal-viewport" data-active={status === 'idle' ? 'false' : 'true'} />
      <SessionHistoryLink connectionId={connectionId} />
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
