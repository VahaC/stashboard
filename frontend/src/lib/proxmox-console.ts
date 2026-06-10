import { api } from './api'

/**
 * V6.6 — opens the browser Proxmox LXC console: an interactive shell *inside* an
 * LXC, reached by SSHing to the Proxmox host and running
 * `pct exec <vmid> -- <shell>`. Reuses the V5.3/V5.7 transport exactly (see
 * {@link file://./container-exec.ts}): a `WebSocket` can't carry the JWT
 * `Authorization` header, so we first POST for a short-lived, single-use ticket
 * (which also binds the chosen command server-side), then open the socket with
 * `?ticket=…`.
 *
 * Wire protocol (identical to the host terminal / container exec):
 *  - browser → server: binary frames = raw stdin bytes; text frames = JSON
 *    control messages (`{"type":"resize","cols","rows"}`).
 *  - server → browser: binary frames = raw terminal output.
 *
 * Note: like the V5.3 SSH host terminal (and unlike Docker exec), live resize is
 * a no-op server-side — the PTY is created at the size reported on connect.
 */
export interface ProxmoxConsoleHandle {
  /** Forwards keystrokes / paste to the LXC. */
  send: (data: string) => void
  /** Sends a resize control message. */
  resize: (cols: number, rows: number) => void
  /** Closes the socket. Safe to call multiple times. */
  close: () => void
}

interface ProxmoxConsoleTicketResponse {
  ticket: string
  expiresInSeconds: number
  webSocketPath: string
}

export interface OpenProxmoxConsoleOptions {
  cols: number
  rows: number
  /** Shell / command to run inside the LXC (default `/bin/bash` server-side). */
  command?: string
  /** Raw terminal output bytes from the LXC, decoded to a string. */
  onData: (chunk: string) => void
  /** The socket opened successfully and the shell is live. */
  onOpen?: () => void
  /** The socket closed. `reason` carries the server's close text when present. */
  onClose?: (reason: string) => void
  /** A transport / handshake error (ticket mint failed, socket errored). */
  onError?: (message: string) => void
}

/**
 * Mints a ticket and opens the LXC-console WebSocket for an LXC on a Proxmox
 * host. Returns a handle for sending input, resizing and closing. All failures
 * are surfaced through {@link OpenProxmoxConsoleOptions.onError}.
 */
export async function openProxmoxConsole(
  connectionId: string,
  vmId: number,
  options: OpenProxmoxConsoleOptions,
): Promise<ProxmoxConsoleHandle> {
  const encoder = new TextEncoder()
  const decoder = new TextDecoder('utf-8')

  let ticketResponse: ProxmoxConsoleTicketResponse
  try {
    const resp = await api.post<ProxmoxConsoleTicketResponse>(
      `/api/proxmox/connections/${connectionId}/lxc/${vmId}/console/ticket`,
      { command: options.command ?? null },
    )
    ticketResponse = resp.data
  } catch (err) {
    const message = extractError(err)
    options.onError?.(message)
    throw new Error(message, { cause: err })
  }

  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  const params = new URLSearchParams({
    ticket: ticketResponse.ticket,
    cols: String(Math.max(1, Math.floor(options.cols))),
    rows: String(Math.max(1, Math.floor(options.rows))),
  })
  const url = `${proto}//${window.location.host}${ticketResponse.webSocketPath}?${params.toString()}`

  const socket = new WebSocket(url)
  socket.binaryType = 'arraybuffer'

  // If the upgrade never completes (e.g. a reverse proxy that doesn't forward
  // WebSocket upgrades), surface an actionable error rather than spinning.
  let opened = false
  const connectTimeout = window.setTimeout(() => {
    if (opened) return
    options.onError?.(
      "Console connection timed out — the WebSocket upgrade didn't complete. "
      + 'If Stashboard is behind a reverse proxy, make sure it forwards WebSocket '
      + 'upgrades (the Upgrade/Connection headers) for /api.',
    )
    try { socket.close() } catch { /* already closing */ }
  }, 15_000)

  socket.onopen = () => {
    opened = true
    window.clearTimeout(connectTimeout)
    options.onOpen?.()
  }
  socket.onmessage = (event) => {
    if (typeof event.data === 'string') {
      options.onData(event.data)
    } else {
      options.onData(decoder.decode(new Uint8Array(event.data as ArrayBuffer)))
    }
  }
  socket.onerror = () => {
    window.clearTimeout(connectTimeout)
    options.onError?.('Console connection error.')
  }
  socket.onclose = (event) => {
    window.clearTimeout(connectTimeout)
    options.onClose?.(event.reason || '')
  }

  return {
    send: (data: string) => {
      if (socket.readyState === WebSocket.OPEN) socket.send(encoder.encode(data))
    },
    resize: (cols: number, rows: number) => {
      if (socket.readyState !== WebSocket.OPEN) return
      socket.send(JSON.stringify({
        type: 'resize',
        cols: Math.max(1, Math.floor(cols)),
        rows: Math.max(1, Math.floor(rows)),
      }))
    },
    close: () => {
      if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) {
        try { socket.close() } catch { /* already closing */ }
      }
    },
  }
}

function extractError(err: unknown): string {
  if (err && typeof err === 'object' && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string } } }).response
    if (response?.data?.error) return response.data.error
  }
  return err instanceof Error ? err.message : 'Failed to start the LXC console.'
}
