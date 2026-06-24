// @novnc/novnc 1.7's package.json maps the bare specifier straight to core/rfb.js
// (its `exports` is the string "./core/rfb.js"), so the deep subpath import the
// older docs show no longer resolves — import the package root.
import RFB from '@novnc/novnc'
import { api } from './api'

/**
 * V8.6 — opens the browser Proxmox **VM** console: the VM analogue of the V6.6 LXC
 * console (see {@link file://./proxmox-console.ts}). A VM has no `pct exec`, so the
 * transport is Proxmox's built-in **VNC** rendered with **noVNC**, relayed
 * server-side so the Proxmox **API token never reaches the browser**.
 *
 * Flow (mirrors the LXC console's ticket → socket dance):
 *  1. POST for a short-lived, single-use Stashboard ticket. The server mints the
 *     Proxmox `vncproxy` session at the same time and returns its one-time ticket as
 *     `password` — the ephemeral RFB credential the guest's VNC server challenges for
 *     (not the API token).
 *  2. Open noVNC against the Stashboard `…/qemu/{vmid}/console/ws?ticket=…` endpoint
 *     with `wsProtocols: ['binary']` and `credentials.password` set to that ticket.
 *     The backend bridges the raw RFB stream to the real Proxmox `vncwebsocket`.
 */
export interface ProxmoxVncConsoleHandle {
  /** Disconnects the VNC session. Safe to call multiple times. */
  close: () => void
  /** Moves keyboard focus to the canvas. */
  focus: () => void
}

interface ProxmoxVncConsoleTicketResponse {
  ticket: string
  password: string
  expiresInSeconds: number
  webSocketPath: string
}

export interface OpenProxmoxVncConsoleOptions {
  /** The element noVNC renders its canvas into. */
  target: HTMLElement
  /** The RFB session connected and is live. */
  onConnect?: () => void
  /** The session closed. `clean` is false for an abrupt drop. */
  onDisconnect?: (clean: boolean) => void
  /** A transport / handshake / auth error (ticket mint failed, relay refused, bad password). */
  onError?: (message: string) => void
}

/**
 * Mints a ticket and opens the VM-console VNC session for a QEMU guest on a Proxmox
 * host. Returns a handle for focusing and closing. All failures are surfaced through
 * {@link OpenProxmoxVncConsoleOptions.onError}.
 */
export async function openProxmoxVncConsole(
  connectionId: string,
  vmId: number,
  options: OpenProxmoxVncConsoleOptions,
): Promise<ProxmoxVncConsoleHandle> {
  let ticketResponse: ProxmoxVncConsoleTicketResponse
  try {
    const resp = await api.post<ProxmoxVncConsoleTicketResponse>(
      `/api/proxmox/connections/${connectionId}/qemu/${vmId}/console/ticket`,
    )
    ticketResponse = resp.data
  } catch (err) {
    const message = extractError(err)
    options.onError?.(message)
    throw new Error(message, { cause: err })
  }

  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  const params = new URLSearchParams({ ticket: ticketResponse.ticket })
  const url = `${proto}//${window.location.host}${ticketResponse.webSocketPath}?${params.toString()}`

  // We create the WebSocket ourselves (rather than letting noVNC open it from the
  // URL) for one reason: when the backend relay fails to open the Proxmox
  // `vncwebsocket`, it closes our socket with the precise reason in the close frame.
  // The Stashboard upgrade itself succeeds (101), so — unlike a failed upgrade —
  // that reason IS delivered to the browser; capturing it here surfaces the exact
  // server-side cause (e.g. "Proxmox refused the VNC websocket with 401…") instead
  // of a generic "disconnected".
  const socket = new WebSocket(url, ['binary'])
  socket.binaryType = 'arraybuffer'

  let connected = false
  let settled = false
  const fail = (message: string) => {
    if (settled) return
    settled = true
    window.clearTimeout(connectTimeout)
    options.onError?.(message)
  }

  // If the upgrade never completes (e.g. a reverse proxy that doesn't forward
  // WebSocket upgrades), surface an actionable error rather than spinning.
  const connectTimeout = window.setTimeout(() => {
    fail(
      "Console connection timed out — the WebSocket upgrade didn't complete. "
      + 'If Stashboard is behind a reverse proxy, make sure it forwards WebSocket '
      + 'upgrades (the Upgrade/Connection headers) for /api.',
    )
    try { socket.close() } catch { /* already closing */ }
  }, 15_000)

  // The backend's close reason wins — it carries the real cause. (A close before
  // `connected` that has no reason falls through to the noVNC disconnect handler.)
  socket.addEventListener('close', (e) => {
    if (connected) return
    if (e.reason) fail(e.reason)
  })

  const rfb = new RFB(options.target, socket, {
    // The RFB password is the one-time vncproxy ticket — noVNC only uses it if the
    // VM's VNC server requires VNC auth (a no-auth VM ignores it).
    credentials: { password: ticketResponse.password },
  })
  // Scale the framebuffer to the panel; don't ask the VM to resize its own display.
  rfb.scaleViewport = true
  rfb.resizeSession = false
  rfb.background = '#0b1020'

  rfb.addEventListener('connect', () => {
    connected = true
    settled = true
    window.clearTimeout(connectTimeout)
    options.onConnect?.()
  })
  rfb.addEventListener('disconnect', (e: Event) => {
    window.clearTimeout(connectTimeout)
    const clean = (e as CustomEvent<{ clean: boolean }>).detail?.clean ?? false
    if (connected) {
      settled = true
      options.onDisconnect?.(clean)
    } else {
      // Closed before the VNC session ever came up, and the close frame carried no
      // reason (a reverse proxy can strip it). Point at the server log with the exact
      // command — that always has the host's precise reason.
      fail('The console connection closed before the VNC session started. The host’s exact reason is in the server log — run: docker logs --tail 100 stashboard-app (look for "vncwebsocket upgrade failed").')
    }
  })
  rfb.addEventListener('securityfailure', (e: Event) => {
    const detail = (e as CustomEvent<{ status: number; reason?: string }>).detail
    fail(detail?.reason || 'VNC authentication failed.')
  })

  return {
    close: () => {
      window.clearTimeout(connectTimeout)
      try { rfb.disconnect() } catch { /* already closing */ }
    },
    focus: () => {
      try { rfb.focus() } catch { /* not connected yet */ }
    },
  }
}

function extractError(err: unknown): string {
  if (err && typeof err === 'object' && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string } } }).response
    if (response?.data?.error) return response.data.error
  }
  return err instanceof Error ? err.message : 'Failed to start the VM console.'
}
