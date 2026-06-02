import { api } from './api'

/**
 * V5.7 — opens the browser container-exec terminal: an interactive shell
 * *inside* a container via the Docker daemon's `exec` API. Reuses the V5.3
 * transport exactly (see {@link file://./host-shell.ts}): a `WebSocket` can't
 * carry the JWT `Authorization` header, so we first POST for a short-lived,
 * single-use ticket (which also binds the chosen command server-side), then
 * open the socket with `?ticket=…`.
 *
 * Wire protocol (identical to the host terminal):
 *  - browser → server: binary frames = raw stdin bytes; text frames = JSON
 *    control messages (`{"type":"resize","cols","rows"}`).
 *  - server → browser: binary frames = raw terminal output.
 */
export interface ContainerExecHandle {
  /** Forwards keystrokes / paste to the container. */
  send: (data: string) => void
  /** Sends a resize control message. Live resize works for exec (the daemon
   *  exposes a resize endpoint), so this is honoured rather than a no-op. */
  resize: (cols: number, rows: number) => void
  /** Closes the socket. Safe to call multiple times. */
  close: () => void
}

interface ContainerExecTicketResponse {
  ticket: string
  expiresInSeconds: number
  webSocketPath: string
}

export interface OpenContainerExecOptions {
  cols: number
  rows: number
  /** Shell / command to run inside the container (default `/bin/sh` server-side). */
  command?: string
  /** Raw terminal output bytes from the container, decoded to a string. */
  onData: (chunk: string) => void
  /** The socket opened successfully and the shell is live. */
  onOpen?: () => void
  /** The socket closed. `reason` carries the server's close text when present. */
  onClose?: (reason: string) => void
  /** A transport / handshake error (ticket mint failed, socket errored). */
  onError?: (message: string) => void
}

/**
 * Mints a ticket and opens the container-exec WebSocket for a container on a
 * connection. Returns a handle for sending input, resizing and closing. All
 * failures are surfaced through {@link OpenContainerExecOptions.onError}.
 */
export async function openContainerExec(
  connectionId: string,
  containerName: string,
  options: OpenContainerExecOptions,
): Promise<ContainerExecHandle> {
  const encoder = new TextEncoder()
  const decoder = new TextDecoder('utf-8')

  let ticketResponse: ContainerExecTicketResponse
  try {
    const resp = await api.post<ContainerExecTicketResponse>(
      `/api/docker/connections/${connectionId}/containers/${encodeURIComponent(containerName)}/exec/ticket`,
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
  return err instanceof Error ? err.message : 'Failed to start container exec.'
}
