import { useAuthStore } from './auth-store'
import type { DockerContainerStatsSample } from './types'

/**
 * V3.4 — opens a chunked NDJSON connection to
 * `/api/services/{serviceId}/docker/watches/{watchId}/stats` and yields one
 * parsed {@link DockerContainerStatsSample} per server-sent frame (~one per
 * second). Mirrors the V3.3 logs client: `fetch` + `ReadableStream` instead
 * of `EventSource` so the JWT `Authorization` header can ride along.
 *
 * Pass `oneShot: true` for a single-snapshot request (CPU% will be null —
 * the daemon has no PreCPUStats baseline to delta against).
 */
export interface DockerStatsStreamHandle {
  abort: () => void
  done: Promise<void>
}

export interface DockerStatsStreamOptions {
  oneShot?: boolean
}

export function streamDockerStats(
  serviceId: string,
  watchId: string,
  options: DockerStatsStreamOptions,
  onSample: (sample: DockerContainerStatsSample) => void,
  onError?: (message: string) => void,
): DockerStatsStreamHandle {
  return streamStatsFromUrl(
    `/api/services/${serviceId}/docker/watches/${watchId}/stats`,
    options, onSample, onError,
  )
}

/**
 * V3.5 — same NDJSON contract as {@link streamDockerStats}, but addressed
 * by `(connectionId, containerName)` instead of `(serviceId, watchId)`.
 * Used by the V3.5 container modal so the Stats tab works for containers
 * that aren't tracked by any watch.
 */
export function streamContainerStats(
  connectionId: string,
  containerName: string,
  options: DockerStatsStreamOptions,
  onSample: (sample: DockerContainerStatsSample) => void,
  onError?: (message: string) => void,
): DockerStatsStreamHandle {
  return streamStatsFromUrl(
    `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}/stats`,
    options, onSample, onError,
  )
}

function streamStatsFromUrl(
  baseUrl: string,
  options: DockerStatsStreamOptions,
  onSample: (sample: DockerContainerStatsSample) => void,
  onError?: (message: string) => void,
): DockerStatsStreamHandle {
  const params = new URLSearchParams()
  if (options.oneShot) params.set('oneShot', 'true')

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
      throw new Error(`Stats stream failed (${resp.status}): ${detail.slice(0, 200)}`)
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
            const parsed = JSON.parse(line)
            if (parsed && typeof parsed === 'object' && 'stream' in parsed && parsed.stream === 'error') {
              onError?.(String(parsed.message ?? 'Stats stream error'))
            } else {
              onSample(parsed as DockerContainerStatsSample)
            }
          } catch {
            onError?.(`Malformed stats frame: ${line}`)
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
    onError?.(err instanceof Error ? err.message : String(err))
  })

  return {
    abort: () => controller.abort(),
    done,
  }
}
