import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { AlertCircle, Play, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { streamContainerStats, type DockerStatsStreamHandle } from '@/lib/docker-stats'
import type { DockerContainerStatsSample } from '@/lib/types'

export type ContainerStatsPanelProps = {
  connectionId: string
  containerName: string
  /** When false, don't auto-start the stream on mount. Defaults to true. */
  autoStart?: boolean
}

const STATS_WINDOW = 120
const SPARKLINE_WIDTH = 240
const SPARKLINE_HEIGHT = 96

const NETWORK_RX_COLOR = '#22c55e'
const NETWORK_TX_COLOR = '#f59e0b'
const BLOCK_READ_COLOR = '#06b6d4'
const BLOCK_WRITE_COLOR = '#ef4444'
const CPU_COLOR = '#22c55e'
const MEMORY_COLOR = '#3b82f6'
const DEFAULT_SPARKLINE_COLOR = '#22c55e'

/**
 * Shared "Stats" panel — opens an NDJSON stats stream against the
 * `(connectionId, containerName)` endpoint, keeps a rolling window of the
 * last {@link STATS_WINDOW} samples, and renders CPU / memory / network /
 * block-I/O tiles plus inline-SVG sparklines.
 */
export function ContainerStatsPanel({
  connectionId, containerName, autoStart = true,
}: ContainerStatsPanelProps) {
  const [samples, setSamples] = useState<DockerContainerStatsSample[]>([])
  const [streaming, setStreaming] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const handleRef = useRef<DockerStatsStreamHandle | null>(null)

  const stopStream = useCallback(() => {
    handleRef.current?.abort()
    handleRef.current = null
    setStreaming(false)
  }, [])

  const startStream = useCallback(() => {
    stopStream()
    setError(null)
    setStreaming(true)
    const handle = streamContainerStats(
      connectionId, containerName, { oneShot: false },
      (sample) => setSamples((prev) => prev.length >= STATS_WINDOW
        ? [...prev.slice(prev.length - STATS_WINDOW + 1), sample]
        : [...prev, sample]),
      (message) => setError(message),
    )
    handleRef.current = handle
    handle.done.then(() => {
      if (handleRef.current === handle) { handleRef.current = null; setStreaming(false) }
    }).catch((err) => {
      setError(err instanceof Error ? err.message : String(err))
      if (handleRef.current === handle) { handleRef.current = null; setStreaming(false) }
    })
  }, [connectionId, containerName, stopStream])

  useEffect(() => {
    if (autoStart) startStream()
    return () => stopStream()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, containerName])

  const latest = samples[samples.length - 1] ?? null
  const previous = samples.length >= 2 ? samples[samples.length - 2] : null
  const rates = useMemo(() => {
    if (!latest || !previous) return null
    const dtMs = new Date(latest.timestampUtc).getTime() - new Date(previous.timestampUtc).getTime()
    if (dtMs <= 0) return null
    const secs = dtMs / 1000
    return {
      rxRate: Math.max(0, (latest.networkRxBytes - previous.networkRxBytes) / secs),
      txRate: Math.max(0, (latest.networkTxBytes - previous.networkTxBytes) / secs),
      readRate: Math.max(0, (latest.blockReadBytes - previous.blockReadBytes) / secs),
      writeRate: Math.max(0, (latest.blockWriteBytes - previous.blockWriteBytes) / secs),
    }
  }, [latest, previous])

  const cpuSeries = useMemo(() => samples.map((s) => s.cpuPercent ?? 0), [samples])
  const memSeries = useMemo(() => samples.map((s) => s.memoryPercent ?? 0), [samples])
  const ioRateSeries = useMemo(() => {
    if (samples.length === 0) {
      return {
        networkRx: [] as number[],
        networkTx: [] as number[],
        blockRead: [] as number[],
        blockWrite: [] as number[],
      }
    }

    const networkRx = [0]
    const networkTx = [0]
    const blockRead = [0]
    const blockWrite = [0]

    for (let idx = 1; idx < samples.length; idx++) {
      const prev = samples[idx - 1]
      const curr = samples[idx]
      const dtMs = new Date(curr.timestampUtc).getTime() - new Date(prev.timestampUtc).getTime()
      if (dtMs <= 0) {
        networkRx.push(networkRx[networkRx.length - 1] ?? 0)
        networkTx.push(networkTx[networkTx.length - 1] ?? 0)
        blockRead.push(blockRead[blockRead.length - 1] ?? 0)
        blockWrite.push(blockWrite[blockWrite.length - 1] ?? 0)
        continue
      }

      const secs = dtMs / 1000
      networkRx.push(Math.max(0, (curr.networkRxBytes - prev.networkRxBytes) / secs))
      networkTx.push(Math.max(0, (curr.networkTxBytes - prev.networkTxBytes) / secs))
      blockRead.push(Math.max(0, (curr.blockReadBytes - prev.blockReadBytes) / secs))
      blockWrite.push(Math.max(0, (curr.blockWriteBytes - prev.blockWriteBytes) / secs))
    }

    return { networkRx, networkTx, blockRead, blockWrite }
  }, [samples])

  return (
    <div className="docker-stats-body">
      <div className="container-modal-stats-header">
        <span className="docker-logs-toolbar-status">
          <span className="docker-logs-toolbar-status-dot" data-state={streaming ? 'streaming' : 'idle'} />
          {streaming ? 'streaming' : 'idle'} · {samples.length} sample{samples.length === 1 ? '' : 's'}
        </span>
        {streaming ? (
          <Button type="button" variant="outline" size="sm" onClick={stopStream}>
            <Square className="h-3.5 w-3.5" /> Stop
          </Button>
        ) : (
          <Button type="button" variant="outline" size="sm" onClick={startStream}>
            <Play className="h-3.5 w-3.5" /> Stream
          </Button>
        )}
      </div>
      {error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
      {!latest && !error && (
        <p className="container-modal-empty">
          {streaming ? 'Waiting for first sample…' : 'No stats yet.'}
        </p>
      )}
      {latest && (
        <div className="docker-stats-grid">
          <StatTile
            label="CPU"
            valueParts={[{ text: formatCpu(latest.cpuPercent), color: CPU_COLOR }]}
            hint={`${latest.onlineCpus} core${latest.onlineCpus === 1 ? '' : 's'}`}
            sparklines={[{ data: cpuSeries, color: CPU_COLOR, label: 'CPU', formatter: formatPercentValue }]}
            max={Math.max(100, latest.onlineCpus * 100)}
          />
          <StatTile
            label="Memory"
            valueParts={[{
              text: `${formatBytes(latest.memoryUsageBytes)} / ${formatBytes(latest.memoryLimitBytes)}`,
              color: MEMORY_COLOR,
            }]}
            hint={latest.memoryPercent !== null ? `${latest.memoryPercent.toFixed(1)}%` : ''}
            sparklines={[{ data: memSeries, color: MEMORY_COLOR, label: 'Memory', formatter: formatPercentValue }]}
            max={100}
          />
          <StatTile
            label="Network ↓ / ↑"
            valueParts={rates
              ? [
                { text: formatBytesPerSec(rates.rxRate), color: NETWORK_RX_COLOR },
                { text: ' / ' },
                { text: formatBytesPerSec(rates.txRate), color: NETWORK_TX_COLOR },
              ]
              : [
                { text: formatBytes(latest.networkRxBytes), color: NETWORK_RX_COLOR },
                { text: ' / ' },
                { text: formatBytes(latest.networkTxBytes), color: NETWORK_TX_COLOR },
                { text: ' total' },
              ]}
            hint={rates ? 'last second' : 'first sample'}
            sparklines={[
              { data: ioRateSeries.networkRx, color: NETWORK_RX_COLOR, label: 'Recv', formatter: formatBytesPerSec },
              { data: ioRateSeries.networkTx, color: NETWORK_TX_COLOR, label: 'Sent', formatter: formatBytesPerSec },
            ]}
          />
          <StatTile
            label="Block I/O R / W"
            valueParts={rates
              ? [
                { text: formatBytesPerSec(rates.readRate), color: BLOCK_READ_COLOR },
                { text: ' / ' },
                { text: formatBytesPerSec(rates.writeRate), color: BLOCK_WRITE_COLOR },
              ]
              : [
                { text: formatBytes(latest.blockReadBytes), color: BLOCK_READ_COLOR },
                { text: ' / ' },
                { text: formatBytes(latest.blockWriteBytes), color: BLOCK_WRITE_COLOR },
                { text: ' total' },
              ]}
            hint={rates ? 'last second' : 'first sample'}
            sparklines={[
              { data: ioRateSeries.blockRead, color: BLOCK_READ_COLOR, label: 'Read', formatter: formatBytesPerSec },
              { data: ioRateSeries.blockWrite, color: BLOCK_WRITE_COLOR, label: 'Write', formatter: formatBytesPerSec },
            ]}
          />
        </div>
      )}
    </div>
  )
}

interface SparklineSeries {
  data: number[]
  label?: string
  color?: string
  formatter?: (value: number) => string
}

interface StatValuePart {
  text: string
  color?: string
}

interface StatTileProps {
  label: string
  value?: ReactNode
  valueParts?: StatValuePart[]
  hint?: string
  sparkline?: number[]
  sparklines?: SparklineSeries[]
  max?: number
}

function StatTile({ label, value, valueParts, hint, sparkline, sparklines, max }: StatTileProps) {
  const series = useMemo(() => {
    const fromSingle: SparklineSeries[] = sparkline ? [{ data: sparkline }] : []
    const fromMany: SparklineSeries[] = sparklines ?? []
    return [...fromSingle, ...fromMany].filter((item) => item.data.length > 1)
  }, [sparkline, sparklines])

  const seriesSummary = useMemo(() => series.map((entry, idx) => {
    const current = entry.data[entry.data.length - 1] ?? 0
    const peak = Math.max(...entry.data, 0)
    const formatter = entry.formatter ?? defaultSeriesFormatter
    return {
      key: `${entry.label ?? `series-${idx}`}-${entry.color ?? DEFAULT_SPARKLINE_COLOR}`,
      color: entry.color ?? DEFAULT_SPARKLINE_COLOR,
      label: entry.label ?? `Series ${idx + 1}`,
      currentText: formatter(current),
      peakText: formatter(peak),
    }
  }), [series])

  return (
    <div className="docker-stats-tile">
      <div className="docker-stats-tile-head">
        <span className="docker-stats-tile-label">{label}</span>
        {hint && <span className="docker-stats-tile-hint">{hint}</span>}
      </div>
      <div className="docker-stats-tile-value">
        {valueParts
          ? valueParts.map((part, idx) => (
            <span key={`${idx}-${part.text}`} style={part.color ? { color: part.color } : undefined}>{part.text}</span>
          ))
          : value}
      </div>
      {series.length > 0 && (
        <>
          <Sparkline series={series} max={max} />
          <div className="docker-stats-series-summary">
            {seriesSummary.map((item) => (
              <div key={item.key} className="docker-stats-series-row">
                <span className="docker-stats-series-name">
                  <span
                    className="docker-stats-series-dot"
                    style={{ backgroundColor: item.color }}
                    aria-hidden="true"
                  />
                  {item.label}
                </span>
                <span className="docker-stats-series-metric">Now {item.currentText}</span>
                <span className="docker-stats-series-metric">Peak {item.peakText}</span>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}

function Sparkline({ series, max }: { series: SparklineSeries[]; max?: number }) {
  const width = SPARKLINE_WIDTH
  const height = SPARKLINE_HEIGHT
  const gridRows = 4
  const gridCols = 6
  const graphPaddingTop = 6
  const graphPaddingBottom = 6
  const graphHeight = height - graphPaddingTop - graphPaddingBottom
  const seriesMax = Math.max(
    max ?? 0,
    ...series.flatMap((entry) => entry.data),
    1,
  )
  const yScale = (value: number): number => {
    const normalized = Math.max(0, Math.min(value, seriesMax)) / seriesMax
    return graphPaddingTop + (1 - normalized) * graphHeight
  }
  const yLabels = [seriesMax, seriesMax * 0.5, 0]

  return (
    <div className="docker-stats-sparkline-wrap">
      <svg
        className="docker-stats-sparkline"
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="none"
        role="img"
        aria-hidden="true"
      >
        <rect x="0" y="0" width={width} height={height} fill="var(--card)" />
        {Array.from({ length: gridRows + 1 }).map((_, idx) => {
          const y = (idx / gridRows) * height
          return (
            <line
              key={`h-${idx}`}
              x1="0"
              y1={y}
              x2={width}
              y2={y}
              className="docker-stats-grid-line"
            />
          )
        })}
        {Array.from({ length: gridCols + 1 }).map((_, idx) => {
          const x = (idx / gridCols) * width
          return (
            <line
              key={`v-${idx}`}
              x1={x}
              y1="0"
              x2={x}
              y2={height}
              className="docker-stats-grid-line docker-stats-grid-line-v"
            />
          )
        })}
        {series.map((entry, idx) => {
          const stepX = entry.data.length > 1 ? width / (entry.data.length - 1) : 0
          const linePoints = entry.data
            .map((value, pointIdx) => `${(pointIdx * stepX).toFixed(1)},${yScale(value).toFixed(1)}`)
            .join(' ')
          const areaPoints = `0,${height} ${linePoints} ${width.toFixed(1)},${height}`
          const lastY = yScale(entry.data[entry.data.length - 1] ?? 0)
          return (
            <g key={`${idx}-${entry.color ?? 'default'}`}>
              <polygon
                points={areaPoints}
                fill={entry.color ?? DEFAULT_SPARKLINE_COLOR}
                className="docker-stats-series-area"
              />
              <polyline
                points={linePoints}
                fill="none"
                stroke={entry.color ?? DEFAULT_SPARKLINE_COLOR}
              />
              <circle
                cx={width}
                cy={lastY}
                r="2.2"
                fill={entry.color ?? DEFAULT_SPARKLINE_COLOR}
              />
            </g>
          )
        })}
      </svg>
      <div className="docker-stats-y-axis" aria-hidden="true">
        {yLabels.map((value, idx) => (
          <span key={`axis-${idx}`}>{formatAxisValue(value)}</span>
        ))}
      </div>
    </div>
  )
}

const defaultSeriesFormatter = (value: number): string => {
  if (!Number.isFinite(value)) return '0'
  return value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)
}

const formatPercentValue = (value: number): string => `${value.toFixed(1)}%`

const formatAxisValue = (value: number): string => {
  if (value <= 0) return '0'
  if (value < 100) return `${value.toFixed(0)}`
  const units = ['k', 'M', 'G', 'T']
  let scaled = value
  let unit = -1
  while (scaled >= 1000 && unit < units.length - 1) {
    scaled /= 1000
    unit += 1
  }
  if (unit < 0) return value.toFixed(0)
  return `${scaled.toFixed(scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2)}${units[unit]}`
}

const formatCpu = (pct: number | null): string => {
  if (pct === null || Number.isNaN(pct)) return '—'
  return `${pct.toFixed(1)}%`
}

const formatBytes = (bytes: number): string => {
  if (!bytes || Number.isNaN(bytes)) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)} ${units[unit]}`
}

const formatBytesPerSec = (rate: number): string => `${formatBytes(rate)}/s`
