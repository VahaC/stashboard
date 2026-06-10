import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertCircle, Plug, PlugZap, ScrollText, TerminalSquare } from 'lucide-react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { Button } from '@/components/ui/button'
import { openProxmoxConsole, type ProxmoxConsoleHandle } from '@/lib/proxmox-console'
// Reuse the Docker host-terminal / exec panel styles verbatim so the LXC
// console is the same surface, not a parallel one.
import '@/styles/docker-instances.css'

type Status = 'idle' | 'connecting' | 'connected' | 'closed' | 'error'

export interface LxcConsolePanelProps {
  connectionId: string
  vmId: number
  /** Guest runtime state — `pct exec` needs a running LXC. */
  isRunning: boolean
  /** Whether the host has SSH credentials configured (the console SSHes in). */
  sshConfigured: boolean
  /** Per-host opt-in (ProxmoxConnection.allowConsole). */
  allowConsole: boolean
  /** Global master switch (StashboardFeatures.allowProxmoxConsole). */
  allowConsoleGlobal: boolean
  /** Whether the Console tab is the active tab. The panel is kept mounted across
   *  tab switches (so the SSH session survives); when it becomes active again we
   *  refit + refocus the terminal. Defaults to true for standalone use. */
  active?: boolean
}

/**
 * V6.6 — the LXC Console tab. The tab is always present on the LXC modal so the
 * affordance is discoverable, but the live xterm.js terminal only renders when
 * the server's global switch is on, this host has opted in, SSH is configured,
 * and the guest is running. It is the LXC analogue of the Docker {@link
 * file://../docker/ContainerExecPanel.tsx} — same gating shape and same UX.
 */
export function LxcConsolePanel({
  connectionId, vmId, isRunning, sshConfigured, allowConsole, allowConsoleGlobal, active = true,
}: LxcConsolePanelProps) {
  if (!allowConsoleGlobal) {
    return (
      <DisabledState
        title="The LXC console is disabled on this server"
        hint="An operator must enable it at Settings → LXC console. It's off by default because the console runs arbitrary commands inside the guest."
      />
    )
  }
  if (!allowConsole) {
    return (
      <DisabledState
        title="The LXC console is not enabled for this host"
        hint="Turn on 'Allow LXC console' in this Proxmox host's settings (Edit host) to open a shell inside its containers."
      />
    )
  }
  if (!sshConfigured) {
    return (
      <DisabledState
        title="This host has no SSH credentials"
        hint="The console SSHes to the Proxmox host and runs 'pct exec' inside the LXC. Add SSH host / username / private key on the Proxmox host (Edit) first."
      />
    )
  }
  if (!isRunning) {
    return (
      <DisabledState
        title="Container is not running"
        hint="The console attaches a shell to a live container — start the LXC first, then reopen this tab."
      />
    )
  }
  return <LiveTerminal connectionId={connectionId} vmId={vmId} active={active} />
}

/** V6.8 — the node SSH console: same gating shape as the LXC console (global
 *  switch + per-host opt-in + SSH) but no running-guest gate, and it opens a
 *  shell **on the Proxmox host** itself (vmId 0) rather than inside an LXC. */
export function NodeConsolePanel({
  connectionId, sshConfigured, allowConsole, allowConsoleGlobal,
}: Omit<LxcConsolePanelProps, 'vmId' | 'isRunning'>) {
  if (!allowConsoleGlobal) {
    return (
      <DisabledState
        title="The Proxmox console is disabled on this server"
        hint="An operator must enable it at Settings → LXC console. It's off by default because the console runs arbitrary commands on the host."
      />
    )
  }
  if (!allowConsole) {
    return (
      <DisabledState
        title="The console is not enabled for this host"
        hint="Turn on 'Allow LXC console' in this Proxmox host's settings (Edit host) to open a shell on the node."
      />
    )
  }
  if (!sshConfigured) {
    return (
      <DisabledState
        title="This host has no SSH credentials"
        hint="The node console SSHes to the Proxmox host and opens a login shell. Add SSH host / username / private key on the Proxmox host (Edit) first."
      />
    )
  }
  return (
    <LiveTerminal
      connectionId={connectionId}
      vmId={0}
      warning="You are about to open a shell on the Proxmox host itself. Every session is audited."
    />
  )
}

function SessionHistoryLink({ connectionId }: { connectionId: string }) {
  return (
    <p className="mt-2 text-xs">
      <Link
        to={`/audit?tab=console&connectionId=${connectionId}`}
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

function LiveTerminal({ connectionId, vmId, warning, active = true }: { connectionId: string; vmId: number; warning?: string; active?: boolean }) {
  const [status, setStatus] = useState<Status>('idle')
  const [error, setError] = useState<string | null>(null)
  const [command, setCommand] = useState('/bin/bash')

  const containerRef = useRef<HTMLDivElement | null>(null)
  const termRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const handleRef = useRef<ProxmoxConsoleHandle | null>(null)
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

    let handle: ProxmoxConsoleHandle
    try {
      handle = await openProxmoxConsole(connectionId, vmId, {
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
      // openProxmoxConsole already surfaced the message via onError when it
      // could; ensure the terminal is cleaned up either way.
      setError(err instanceof Error ? err.message : 'Failed to start the LXC console.')
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

    // Forward keystrokes / paste to the LXC.
    term.onData((data) => handleRef.current?.send(data))

    // Keep the panel-side terminal fit; the PTY size is set on connect server
    // side (SSH.NET can't live-resize, same as the V5.3 host terminal), but we
    // still send the request so a future transport can honour it.
    const observer = new ResizeObserver(() => {
      try {
        fit.fit()
        handleRef.current?.resize(term.cols, term.rows)
      } catch { /* mid-teardown */ }
    })
    observer.observe(containerRef.current)
    resizeObserverRef.current = observer
  }, [connectionId, vmId, command])

  // Tear everything down on unmount (modal closed). The panel is kept mounted
  // across tab switches, so this fires only when the modal itself closes.
  useEffect(() => () => teardown(), [teardown])

  // When the tab is re-shown after being hidden, the container went 0×0 →
  // visible; refit the terminal to the restored size and refocus it. No-op until
  // a session exists.
  useEffect(() => {
    if (!active) return
    try {
      fitRef.current?.fit()
      if (termRef.current && handleRef.current) {
        handleRef.current.resize(termRef.current.cols, termRef.current.rows)
        termRef.current.focus()
      }
    } catch { /* mid-teardown / not laid out */ }
  }, [active])

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
            placeholder="/bin/bash"
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
          {warning ?? 'You are about to open a shell inside this container. Every session is audited.'}
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
