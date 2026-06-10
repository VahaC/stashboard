import { useAuthStore } from './auth-store'

/**
 * V6.7.1 — one-click "Update now" for Proxmox. POSTs to the apply endpoint and
 * yields the apt output as it streams back (NDJSON over fetch, the same contract
 * the Docker log stream uses — chosen so the JWT bearer header rides along and
 * the browser's `ReadableStream` gives incremental delivery). `vmId === 0`
 * upgrades the node itself; any other value upgrades that LXC.
 */

/** One frame from the apply stream. */
export type ProxmoxUpdateFrame =
  /** A line of apt output. */
  | { stream: 'stdout'; message: string }
  /** Terminal frame written when the run finishes. */
  | { stream: 'end'; success: boolean; exitStatus: number | null; nonDebian: boolean; error: string | null }
  /** Transport / unexpected error. */
  | { stream: 'error'; message: string }

export interface ProxmoxUpdateStreamHandle {
  /** Aborts the underlying fetch. Safe to call multiple times. */
  abort: () => void
  /** Resolves when the stream ends (naturally or aborted); never rejects —
   *  failures arrive as an `error` frame. */
  done: Promise<void>
}

export function applyProxmoxUpdates(
  connectionId: string,
  vmId: number,
  onFrame: (frame: ProxmoxUpdateFrame) => void,
): ProxmoxUpdateStreamHandle {
  const url = vmId === 0
    ? `/api/proxmox/connections/${connectionId}/node/update`
    : `/api/proxmox/connections/${connectionId}/lxc/${vmId}/update`
  return streamNdjson(url, undefined, onFrame)
}

/**
 * V6.11 — one frame from the bulk apply stream. The server iterates the requested
 * LXCs in turn, bracketing each guest's apt output with `guest-start` /
 * `guest-end` and closing with a single `all-done` summary.
 */
export type ProxmoxBulkUpdateFrame =
  /** A guest's update run is starting. */
  | { stream: 'guest-start'; vmId: number; name: string }
  /** A line of apt output for the named guest. */
  | { stream: 'stdout'; vmId: number; message: string }
  /** A guest's update run finished. */
  | { stream: 'guest-end'; vmId: number; name: string; success: boolean; exitStatus: number | null; nonDebian: boolean; error: string | null }
  /** Terminal frame: the whole run finished. */
  | { stream: 'all-done'; total: number; succeeded: number; failed: number }
  /** Transport / unexpected error (also covers gate failures before streaming). */
  | { stream: 'error'; message: string }

/**
 * V6.11 — apply pending updates across several LXCs on a host in one streaming
 * run. POSTs the selected `vmIds` and yields the bulk frames as they arrive.
 * Same NDJSON-over-fetch contract as {@link applyProxmoxUpdates}.
 */
export function applyProxmoxBulkUpdates(
  connectionId: string,
  vmIds: number[],
  onFrame: (frame: ProxmoxBulkUpdateFrame) => void,
): ProxmoxUpdateStreamHandle {
  const url = `/api/proxmox/connections/${connectionId}/lxc/update/bulk`
  return streamNdjson(url, { vmIds }, onFrame)
}

/** Shared NDJSON-over-fetch reader for the single + bulk apply streams. Carries
 *  the JWT bearer header, parses one JSON frame per line, and surfaces transport
 *  failures + gate errors as an `error` frame (never rejects). */
function streamNdjson<TFrame extends { stream: string }>(
  url: string,
  body: unknown | undefined,
  onFrame: (frame: TFrame) => void,
): ProxmoxUpdateStreamHandle {
  const controller = new AbortController()
  const token = useAuthStore.getState().accessToken

  const done = (async () => {
    let resp: Response
    try {
      resp = await fetch(url, {
        method: 'POST',
        signal: controller.signal,
        headers: {
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
          ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
      })
    } catch (err) {
      if (controller.signal.aborted) return
      throw err
    }

    if (!resp.ok) {
      // Gate failures arrive as a normal JSON error before streaming starts.
      const detail = await resp.text().catch(() => '')
      let message = `Update failed (${resp.status}).`
      try {
        const parsed = JSON.parse(detail) as { error?: string }
        if (parsed.error) message = parsed.error
      } catch { /* non-JSON body — keep the status message */ }
      onFrame({ stream: 'error', message } as unknown as TFrame)
      return
    }
    if (!resp.body) return

    const reader = resp.body.getReader()
    const decoder = new TextDecoder('utf-8')
    let buffer = ''
    try {
      while (true) {
        const { value, done: streamDone } = await reader.read()
        if (streamDone) break
        buffer += decoder.decode(value, { stream: true })
        let newlineIdx: number
        while ((newlineIdx = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, newlineIdx)
          buffer = buffer.slice(newlineIdx + 1)
          if (!line) continue
          try {
            onFrame(JSON.parse(line) as TFrame)
          } catch {
            onFrame({ stream: 'error', message: `(malformed frame) ${line}` } as unknown as TFrame)
          }
        }
      }
    } catch (err) {
      if (!controller.signal.aborted) throw err
    } finally {
      try { reader.releaseLock() } catch { /* already released */ }
    }
  })().catch((err) => {
    if (controller.signal.aborted) return
    onFrame({ stream: 'error', message: err instanceof Error ? err.message : String(err) } as unknown as TFrame)
  })

  return {
    abort: () => controller.abort(),
    done,
  }
}
