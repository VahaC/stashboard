import { api } from './api'

/**
 * V6.12 — opens the browser LXC live-logs tail: a *read-only* stream of a guest's
 * system journal, reached by SSHing to the Proxmox host and running
 * `pct exec <vmid> -- journalctl -f` (with a `/var/log` fallback). Reuses the V6.6
 * console transport exactly (see {@link file://./proxmox-console.ts}): a
 * `WebSocket` can't carry the JWT `Authorization` header, so we first POST for a
 * short-lived, single-use ticket, then open the socket with `?ticket=…`.
 *
 * Unlike the console this is one-directional — the server streams raw journal
 * bytes (binary frames) and the client never sends input. The bytes are decoded,
 * split on newlines and surfaced one line at a time so a line-oriented viewport
 * (the Docker-style logs panel) can render them.
 */
export interface ProxmoxLogsHandle {
  /** Closes the socket. Safe to call multiple times. */
  close: () => void
}

interface ProxmoxLogsTicketResponse {
  ticket: string
  expiresInSeconds: number
  webSocketPath: string
}

export interface OpenProxmoxLogsOptions {
  /** Follow the journal (`-f`) vs a one-shot snapshot (for Download). Default true. */
  follow?: boolean
  /** One decoded, ANSI-stripped journal line (no trailing newline). */
  onLine: (line: string) => void
  /** The socket opened and the tail is live. */
  onOpen?: () => void
  /** The socket closed. `reason` carries the server's close text when present. */
  onClose?: (reason: string) => void
  /** A transport / handshake error (ticket mint failed, socket errored). */
  onError?: (message: string) => void
}

/**
 * Mints a ticket and opens the logs WebSocket for an LXC on a Proxmox host.
 * Returns a handle for closing. All failures are surfaced through
 * {@link OpenProxmoxLogsOptions.onError}.
 */
export async function openProxmoxLogs(
  connectionId: string,
  vmId: number,
  options: OpenProxmoxLogsOptions,
): Promise<ProxmoxLogsHandle> {
  const decoder = new TextDecoder('utf-8')
  const follow = options.follow ?? true

  let ticketResponse: ProxmoxLogsTicketResponse
  try {
    const resp = await api.post<ProxmoxLogsTicketResponse>(
      `/api/proxmox/connections/${connectionId}/lxc/${vmId}/logs/ticket`,
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
    follow: String(follow),
  })
  const url = `${proto}//${window.location.host}${ticketResponse.webSocketPath}?${params.toString()}`

  const socket = new WebSocket(url)
  socket.binaryType = 'arraybuffer'

  // Carry-over for a line that spans two frames.
  let buffer = ''
  const emitChunk = (text: string) => {
    buffer += text
    let nl: number
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl).replace(/\r$/, '')
      buffer = buffer.slice(nl + 1)
      options.onLine(stripAnsi(line))
    }
  }

  // If the upgrade never completes (e.g. a reverse proxy that doesn't forward
  // WebSocket upgrades), surface an actionable error rather than spinning.
  let opened = false
  const connectTimeout = window.setTimeout(() => {
    if (opened) return
    options.onError?.(
      "Log stream connection timed out — the WebSocket upgrade didn't complete. "
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
      emitChunk(event.data)
    } else {
      emitChunk(decoder.decode(new Uint8Array(event.data as ArrayBuffer), { stream: true }))
    }
  }
  socket.onerror = () => {
    window.clearTimeout(connectTimeout)
    options.onError?.('Log stream connection error.')
  }
  socket.onclose = (event) => {
    window.clearTimeout(connectTimeout)
    // Flush any trailing partial line (a final tail line without a newline).
    if (buffer) { options.onLine(stripAnsi(buffer.replace(/\r$/, ''))); buffer = '' }
    options.onClose?.(event.reason || '')
  }

  return {
    close: () => {
      if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) {
        try { socket.close() } catch { /* already closing */ }
      }
    },
  }
}

/** Strips CSI/SGR escape sequences so a plain line viewport stays clean. The
 *  server already disables journald colour (SYSTEMD_COLORS=0); this is defensive. */
// eslint-disable-next-line no-control-regex
const ANSI_RE = /\x1b\[[0-9;?]*[ -/]*[@-~]/g
function stripAnsi(s: string): string {
  return s.replace(ANSI_RE, '')
}

function extractError(err: unknown): string {
  if (err && typeof err === 'object' && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string } } }).response
    if (response?.data?.error) return response.data.error
  }
  return err instanceof Error ? err.message : 'Failed to start the LXC log stream.'
}
