import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertCircle, Expand, Maximize2, Minimize2, MonitorPlay, Plug, PlugZap, Shrink } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { openProxmoxVncConsole, type ProxmoxVncConsoleHandle } from '@/lib/proxmox-vnc-console'
import { DisabledState, SessionHistoryLink } from '@/components/proxmox/LxcConsolePanel'
// Reuse the same console surface styles as the LXC console so the VM console is the
// same surface, not a parallel one.
import '@/styles/docker-instances.css'

type Status = 'idle' | 'connecting' | 'connected' | 'closed' | 'error'

export interface VmConsolePanelProps {
  connectionId: string
  vmId: number
  /** Guest runtime state — VNC needs a running VM. */
  isRunning: boolean
  /** Per-host opt-in (ProxmoxConnection.allowConsole). */
  allowConsole: boolean
  /** Global master switch (StashboardFeatures.allowProxmoxConsole). */
  allowConsoleGlobal: boolean
  /** Whether the Console tab is the active tab. The panel is kept mounted across tab
   *  switches (so the VNC session survives); defaults to true for standalone use. */
  active?: boolean
}

/**
 * V8.6 — the VM Console tab: the VM analogue of the V6.6 {@link
 * file://./LxcConsolePanel.tsx}. Same gating shape and same UX, but a VM has no
 * `pct exec`, so it renders Proxmox's built-in **VNC** screen with noVNC (relayed
 * server-side) instead of an xterm SSH shell. The SSH-configured gate is dropped —
 * VNC uses the API token, not SSH.
 */
export function VmConsolePanel({
  connectionId, vmId, isRunning, allowConsole, allowConsoleGlobal, active = true,
}: VmConsolePanelProps) {
  if (!allowConsoleGlobal) {
    return (
      <DisabledState
        title="The guest console is disabled on this server"
        hint="An operator must enable it at Settings → Guest console. It's off by default because the console gives full interactive access to the guest."
      />
    )
  }
  if (!allowConsole) {
    return (
      <DisabledState
        title="The console is not enabled for this host"
        hint="Turn on 'Allow guest console' in this Proxmox host's settings (Edit host) to open the VNC screen of its VMs."
      />
    )
  }
  if (!isRunning) {
    return (
      <DisabledState
        title="VM is not running"
        hint="The console attaches to the VM's live VNC display — start the VM first, then reopen this tab."
      />
    )
  }
  return <LiveConsole connectionId={connectionId} vmId={vmId} active={active} />
}

function LiveConsole({ connectionId, vmId, active = true }: { connectionId: string; vmId: number; active: boolean }) {
  const [status, setStatus] = useState<Status>('idle')
  const [error, setError] = useState<string | null>(null)
  // Two ways to enlarge the screen: `expanded` fills the browser viewport
  // (100vw×100vh, still inside the page), `isFullscreen` uses the real Fullscreen API.
  const [expanded, setExpanded] = useState(false)
  const [isFullscreen, setIsFullscreen] = useState(false)

  const panelRef = useRef<HTMLDivElement | null>(null)
  const containerRef = useRef<HTMLDivElement | null>(null)
  const handleRef = useRef<ProxmoxVncConsoleHandle | null>(null)
  // Guards the async ticket→socket window so a disconnect mid-connect doesn't leave a
  // live session behind.
  const cancelledRef = useRef(false)

  // noVNC's scaleViewport recomputes on a window resize; fire one after the panel
  // changes size (the 100vw/100vh toggle doesn't change the window) so the canvas
  // rescales to fill, then refocus for keyboard input.
  const nudgeResize = useCallback(() => {
    window.setTimeout(() => {
      window.dispatchEvent(new Event('resize'))
      handleRef.current?.focus()
    }, 60)
  }, [])

  // The two enlarge modes are mutually exclusive: clicking one while the other is
  // active switches between them.
  const toggleExpand = useCallback(() => {
    if (document.fullscreenElement) void document.exitFullscreen() // full screen → fill window
    setExpanded((v) => !v)
  }, [])

  // "Fill window" maximises the whole modal — apply the class on the dialog ancestor
  // (so its centring transform is neutralised; a position:fixed child can't escape
  // it). Driven by an effect so it's DOM-only (no setState): the class is on only
  // while the user wants it AND the Console tab is active, so leaving the tab drops
  // the fill-window layout without losing the user's intent, and it cleans up on
  // unmount (modal close).
  useEffect(() => {
    const dlg = panelRef.current?.closest('.container-modal-content')
    if (!dlg) return
    dlg.classList.toggle('container-modal-content--vnc-max', expanded && active)
    nudgeResize()
    return () => dlg.classList.remove('container-modal-content--vnc-max')
  }, [expanded, active, nudgeResize])

  const toggleFullscreen = useCallback(() => {
    const el = panelRef.current
    if (!el) return
    if (document.fullscreenElement) {
      void document.exitFullscreen()
    } else {
      setExpanded(false) // fill window → full screen
      void el.requestFullscreen?.()
    }
  }, [])

  // Keep `isFullscreen` in sync (covers ESC / browser-driven exit) and rescale.
  useEffect(() => {
    const onChange = () => {
      setIsFullscreen(document.fullscreenElement === panelRef.current)
      nudgeResize()
    }
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [nudgeResize])

  const teardown = useCallback(() => {
    cancelledRef.current = true
    handleRef.current?.close()
    handleRef.current = null
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

    let handle: ProxmoxVncConsoleHandle
    try {
      handle = await openProxmoxVncConsole(connectionId, vmId, {
        target: containerRef.current,
        onConnect: () => {
          if (cancelledRef.current) return
          setStatus('connected')
          handleRef.current?.focus()
        },
        onDisconnect: () => {
          if (cancelledRef.current) return
          setStatus('closed')
        },
        onError: (message) => {
          if (cancelledRef.current) return
          setError(message)
          setStatus('error')
        },
      })
    } catch (err) {
      // openProxmoxVncConsole already surfaced the message via onError when it could.
      setError(err instanceof Error ? err.message : 'Failed to start the VM console.')
      setStatus('error')
      return
    }

    if (cancelledRef.current) {
      handle.close()
      return
    }
    handleRef.current = handle
  }, [connectionId, vmId])

  // Tear everything down on unmount (modal closed). The panel is kept mounted across
  // tab switches, so this fires only when the modal itself closes.
  useEffect(() => () => teardown(), [teardown])

  // When the tab is re-shown after being hidden, refocus the canvas so keyboard input
  // is captured again. No-op until a session exists.
  useEffect(() => {
    if (!active) return
    if (handleRef.current && status === 'connected') handleRef.current.focus()
  }, [active, status])

  const connecting = status === 'connecting'
  const connected = status === 'connected'

  return (
    <div ref={panelRef} className={cn('host-terminal-panel', expanded && 'host-terminal-panel--expanded')}>
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
          <MonitorPlay className="h-3.5 w-3.5 inline" /> You are about to open this VM's VNC screen — full keyboard / mouse control. Every session is audited.
        </span>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={toggleExpand}
          title={expanded ? 'Restore size' : 'Fill the window'}
          aria-label={expanded ? 'Restore size' : 'Fill the window'}
        >
          {expanded ? <Minimize2 className="h-3.5 w-3.5" /> : <Maximize2 className="h-3.5 w-3.5" />}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={toggleFullscreen}
          title={isFullscreen ? 'Exit full screen' : 'Full screen'}
          aria-label={isFullscreen ? 'Exit full screen' : 'Full screen'}
        >
          {isFullscreen ? <Shrink className="h-3.5 w-3.5" /> : <Expand className="h-3.5 w-3.5" />}
        </Button>
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
