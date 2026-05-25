import { api } from './api'

/**
 * V5.3 — opens the browser host terminal: an interactive SSH shell on the
 * Docker host. A `WebSocket` can't carry the JWT `Authorization` header (the
 * same limitation that keeps log / stat streaming on NDJSON-over-fetch — see
 * {@link file://./docker-logs.ts}), so we first POST for a short-lived,
 * single-use ticket over the authenticated `api` client, then open the socket
 * with `?ticket=…`.
 *
 * Wire protocol:
 *  - browser → server: binary frames = raw stdin bytes; text frames = JSON
 *    control messages (currently `{"type":"resize","cols","rows"}`).
 *  - server → browser: binary frames = raw terminal output.
 */
export interface HostShellHandle {
  /** Forwards keystrokes / paste to the host. */
  send: (data: string) => void
  /** Sends a resize control message (best-effort — live resize may be a no-op
   *  server-side on SSH.NET 2024.2.0; the initial size is always honoured). */
  resize: (cols: number, rows: number) => void
  /** Closes the socket. Safe to call multiple times. */
  close: () => void
}

interface HostShellTicketResponse {
  ticket: string
  expiresInSeconds: number
  webSocketPath: string
}

export interface OpenHostShellOptions {
  cols: number
  rows: number
  /** Raw terminal output bytes from the host, decoded to a string. */
  onData: (chunk: string) => void
  /** The socket opened successfully and the shell is live. */
  onOpen?: () => void
  /** The socket closed. `reason` carries the server's close text when present. */
  onClose?: (reason: string) => void
  /** A transport / handshake error (ticket mint failed, socket errored). */
  onError?: (message: string) => void
}

/**
 * Mints a ticket and opens the host-terminal WebSocket for a connection.
 * Returns a handle for sending input, resizing and closing. All failures are
 * surfaced through {@link OpenHostShellOptions.onError}.
 */
export async function openHostShell(
  connectionId: string,
  options: OpenHostShellOptions,
): Promise<HostShellHandle> {
  const encoder = new TextEncoder()
  const decoder = new TextDecoder('utf-8')

  let ticketResponse: HostShellTicketResponse
  try {
    const resp = await api.post<HostShellTicketResponse>(
      `/api/docker/connections/${connectionId}/host-shell/ticket`,
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
  // WebSocket upgrades), the socket sits in CONNECTING forever. Surface an
  // actionable error rather than spinning indefinitely.
  let opened = false
  const connectTimeout = window.setTimeout(() => {
    if (opened) return
    options.onError?.(
      "Terminal connection timed out — the WebSocket upgrade didn't complete. "
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
    options.onError?.('Terminal connection error.')
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
  return err instanceof Error ? err.message : 'Failed to start host terminal.'
}
