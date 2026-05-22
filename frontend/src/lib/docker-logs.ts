import { useAuthStore } from './auth-store'
import type { DockerLogLine, DockerLogStreamOptions } from './types'

/**
 * V3.3 — opens a chunked NDJSON connection to
 * `/api/services/{serviceId}/docker/watches/{watchId}/logs` and yields one
 * parsed {@link DockerLogLine} per line received from the server. The
 * stream stays open until the caller aborts via the returned `AbortController`.
 *
 * NDJSON over fetch was chosen over EventSource so the JWT bearer header can
 * ride along on the request — EventSource doesn't accept custom headers, and
 * a query-string token leaks into proxy logs. The browser's `ReadableStream`
 * gives us the same incremental delivery semantics without the extra ceremony
 * of SignalR.
 */
export interface DockerLogsStreamHandle {
  /** Aborts the underlying fetch and closes the reader. Safe to call multiple times. */
  abort: () => void
  /** Resolves when the stream ends naturally (follow=false drained, or
   *  server closed) or rejects with the underlying error. Cancellation does
   *  not reject — it resolves. */
  done: Promise<void>
}

export function streamDockerLogs(
  serviceId: string,
  watchId: string,
  options: DockerLogStreamOptions,
  onLine: (line: DockerLogLine) => void,
): DockerLogsStreamHandle {
  return streamLogsFromUrl(
    `/api/services/${serviceId}/docker/watches/${watchId}/logs`,
    options,
    onLine,
  )
}

/**
 * V3.5 — same NDJSON contract as {@link streamDockerLogs}, but addressed
 * by `(connectionId, containerName)` instead of `(serviceId, watchId)`.
 * Used by the V3.5 container modal so the Logs tab works for containers
 * that aren't tracked by any watch.
 */
export function streamContainerLogs(
  connectionId: string,
  containerName: string,
  options: DockerLogStreamOptions,
  onLine: (line: DockerLogLine) => void,
): DockerLogsStreamHandle {
  return streamLogsFromUrl(
    `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}/logs`,
    options,
    onLine,
  )
}

function streamLogsFromUrl(
  baseUrl: string,
  options: DockerLogStreamOptions,
  onLine: (line: DockerLogLine) => void,
): DockerLogsStreamHandle {
  const params = new URLSearchParams()
  if (options.follow !== undefined) params.set('follow', String(options.follow))
  if (options.tail !== undefined) params.set('tail', String(options.tail))
  if (options.since !== undefined) params.set('since', String(options.since))
  if (options.timestamps !== undefined) params.set('timestamps', String(options.timestamps))
  if (options.stdout !== undefined) params.set('stdout', String(options.stdout))
  if (options.stderr !== undefined) params.set('stderr', String(options.stderr))

  const controller = new AbortController()
  const token = useAuthStore.getState().accessToken
  const url = `${baseUrl}?${params.toString()}`

  const done = (async () => {
    let resp: Response
    try {
      resp = await fetch(url, {
        method: 'GET',
        signal: controller.signal,
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      })
    } catch (err) {
      if (controller.signal.aborted) return
      throw err
    }
    if (!resp.ok) {
      const detail = await resp.text().catch(() => '')
      throw new Error(`Log stream failed (${resp.status}): ${detail.slice(0, 200)}`)
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
        // The server flushes one NDJSON record per `\n`. A line that spans
        // two TCP chunks shows up here as a partial — keep it in the buffer
        // until the next chunk completes it.
        let newlineIdx: number
        while ((newlineIdx = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, newlineIdx)
          buffer = buffer.slice(newlineIdx + 1)
          if (!line) continue
          try {
            onLine(JSON.parse(line) as DockerLogLine)
          } catch {
            // Malformed line — surface as an error frame so the user sees
            // *something* rather than silent corruption.
            onLine({ stream: 'error', timestamp: null, message: `(malformed log frame) ${line}` })
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
    onLine({ stream: 'error', timestamp: null, message: err instanceof Error ? err.message : String(err) })
  })

  return {
    abort: () => controller.abort(),
    done,
  }
}
