import { useMemo, useState } from 'react'
import { AlertCircle, ChevronDown, Clock } from 'lucide-react'
import type { ResponseSample, Service, TargetUptimeMetrics, UptimeIncident } from '@/lib/types'
import { useHealthHistory } from '@/lib/queries'
import { cn } from '@/lib/utils'

/**
 * V10.1 — uptime history & analytics on the Uptime tab. Renders, per probed URL
 * (main + additional) as an independently collapsible section, the 24 h / 7 d / 30 d uptime %, a
 * response-time sparkline (hand-rolled inline SVG, reusing the V3.4 stats-panel approach — no chart
 * library), and the incident log of offline spans.
 * All derived from the retained `HealthCheckEvent` rows via `GET /services/{id}/health/metrics`.
 */
export function UptimeHistorySection({ service }: { service: Service }) {
  const { data, isLoading, error } = useHealthHistory(service.id, true)

  if (error) {
    return (
      <p className="service-modal-field-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> Couldn’t load uptime history.
      </p>
    )
  }

  if (isLoading || !data) {
    return <p className="uptime-empty">Loading uptime history…</p>
  }

  const mainLabel = service.healthCheckUrl || service.mainUrl
  const hasAny = data.main.eventCount > 0 || (data.additional?.eventCount ?? 0) > 0

  if (!hasAny) {
    return (
      <p className="uptime-empty">
        No uptime history yet — it builds up as Stashboard probes this service. Try <strong>Check
        now</strong> above, or wait for the next scan.
      </p>
    )
  }

  return (
    <div className="uptime-history">
      <UptimeTarget label={mainLabel} metrics={data.main} />
      {data.additional && service.additionalUrl && (
        <UptimeTarget label={service.additionalUrl} metrics={data.additional} additional />
      )}
      <p className="uptime-retention-note">
        History is retained for {data.retentionDays} days, then pruned automatically.
      </p>
    </div>
  )
}

function UptimeTarget({ label, metrics, additional }: { label: string; metrics: TargetUptimeMetrics; additional?: boolean }) {
  return (
    <div className="uptime-target">
      <div className="uptime-target-head">
        <span className="uptime-target-kind">{additional ? 'Additional URL' : 'Main URL'}</span>
        <span className="uptime-target-url" title={label}>{label}</span>
      </div>

      <div className="uptime-stat-row">
        <UptimeStat label="24 h" value={metrics.uptime24h} />
        <UptimeStat label="7 d" value={metrics.uptime7d} />
        <UptimeStat label="30 d" value={metrics.uptime30d} />
      </div>

      <ResponseSparkline samples={metrics.responseSamples} />

      <IncidentLog incidents={metrics.incidents} total={metrics.incidentCount} />
    </div>
  )
}

function UptimeStat({ label, value }: { label: string; value: number | null }) {
  const color = uptimeColor(value)
  return (
    <div className="uptime-stat">
      <span className="uptime-stat-label">{label}</span>
      <span className="uptime-stat-value" style={{ color }}>
        {value == null ? '—' : `${formatUptime(value)}%`}
      </span>
    </div>
  )
}

function ResponseSparkline({ samples }: { samples: ResponseSample[] }) {
  const points = useMemo(() => samples.map((s) => s.responseTimeMs), [samples])
  const [hover, setHover] = useState<number | null>(null)
  const width = 260
  const height = 64
  const padY = 6

  if (points.length < 2) {
    return <p className="uptime-spark-empty">Not enough response-time samples yet for a trend.</p>
  }

  const max = Math.max(...points, 1)
  const graphH = height - padY * 2
  const stepX = width / (points.length - 1)
  const yScale = (v: number) => padY + (1 - Math.max(0, Math.min(v, max)) / max) * graphH
  const line = points.map((v, i) => `${(i * stepX).toFixed(1)},${yScale(v).toFixed(1)}`).join(' ')
  const area = `0,${height} ${line} ${width.toFixed(1)},${height}`
  const lastY = yScale(points[points.length - 1])
  const current = points[points.length - 1]
  const peak = Math.max(...points)

  // Map the cursor's horizontal position to the nearest sample index. The SVG uses
  // preserveAspectRatio="none", so viewBox x maps linearly to the rendered width.
  const onMove = (e: React.MouseEvent<HTMLDivElement>) => {
    const rect = e.currentTarget.getBoundingClientRect()
    if (rect.width <= 0) return
    const rel = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
    setHover(Math.round(rel * (points.length - 1)))
  }

  const hoverRel = hover != null ? hover / (points.length - 1) : 0
  const hoverX = hover != null ? hover * stepX : 0
  const hoverY = hover != null ? yScale(points[hover]) : 0
  // Keep the tooltip inside the plot at the edges.
  const tipTransform = hoverRel < 0.12 ? 'translateX(0)' : hoverRel > 0.88 ? 'translateX(-100%)' : 'translateX(-50%)'

  return (
    <div className="uptime-spark">
      <div className="uptime-spark-head">
        <span className="uptime-spark-title">Response time</span>
        <span className="uptime-spark-metrics">Now {current} ms · Peak {peak} ms</span>
      </div>
      <div className="docker-stats-sparkline-wrap uptime-spark-wrap">
        <div className="uptime-spark-plot" onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
          <svg
            className="docker-stats-sparkline"
            viewBox={`0 0 ${width} ${height}`}
            preserveAspectRatio="none"
            role="img"
            aria-hidden="true"
          >
            <rect x="0" y="0" width={width} height={height} fill="var(--card)" />
            {[0, 1, 2, 3, 4].map((i) => {
              const y = (i / 4) * height
              return <line key={i} x1="0" y1={y} x2={width} y2={y} className="docker-stats-grid-line" />
            })}
            <polygon points={area} fill="var(--status-up)" className="docker-stats-series-area" />
            <polyline points={line} fill="none" stroke="var(--status-up)" />
            <circle cx={width} cy={lastY} r="2.2" fill="var(--status-up)" />
            {hover != null && (
              <g>
                <line x1={hoverX} y1="0" x2={hoverX} y2={height} className="uptime-spark-cursor" />
                <circle cx={hoverX} cy={hoverY} r="3" className="uptime-spark-cursor-dot" />
              </g>
            )}
          </svg>
          {hover != null && (
            <div className="uptime-spark-tooltip" style={{ left: `${hoverRel * 100}%`, transform: tipTransform }}>
              <span className="uptime-spark-tooltip-ms">{points[hover]} ms</span>
              <span className="uptime-spark-tooltip-time">{formatDateTime(samples[hover].timestampUtc)}</span>
            </div>
          )}
        </div>
        <div className="docker-stats-y-axis" aria-hidden="true">
          <span>{max}</span>
          <span>{Math.round(max / 2)}</span>
          <span>0</span>
        </div>
        <div className="uptime-spark-xaxis">
          <span>{formatAxisTime(samples[0].timestampUtc)}</span>
          <span>{formatAxisTime(samples[samples.length - 1].timestampUtc)}</span>
        </div>
      </div>
    </div>
  )
}

function IncidentLog({ incidents, total }: { incidents: UptimeIncident[]; total: number }) {
  // Collapsed by default — a flapping service can have a long incident list.
  const [open, setOpen] = useState(false)

  return (
    <div className="uptime-incidents">
      <button
        type="button"
        className="uptime-incidents-head"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        <ChevronDown className={cn('uptime-incidents-chevron', !open && 'uptime-incidents-chevron-collapsed')} aria-hidden />
        <span className="uptime-incidents-title">
          Incidents <span className="service-modal-label-help">(last 30 days)</span>
        </span>
        <span className="uptime-incidents-count" data-clear={total === 0}>
          {total === 0 ? 'none' : total}
        </span>
      </button>
      {open && (total === 0 ? (
        <p className="uptime-incidents-empty">No downtime recorded. 🎉</p>
      ) : (
        <ul className="uptime-incidents-list">
          {incidents.map((inc, i) => (
            <li key={`${inc.startUtc}-${i}`} className="uptime-incident" data-ongoing={inc.ongoing}>
              <span className="uptime-incident-dot" aria-hidden />
              <span className="uptime-incident-main">
                <span className="uptime-incident-when">
                  {formatDateTime(inc.startUtc)}
                  <span className="uptime-incident-arrow"> → </span>
                  {inc.ongoing
                    ? <span className="uptime-incident-ongoing">now (ongoing)</span>
                    : formatDateTime(inc.endUtc!)}
                </span>
                {inc.error && <span className="uptime-incident-error" title={inc.error}>{inc.error}</span>}
              </span>
              <span className="uptime-incident-duration">
                <Clock className="h-3 w-3 inline" />
                {formatDuration(inc.durationSeconds)}
              </span>
            </li>
          ))}
          {total > incidents.length && (
            <li className="uptime-incidents-more">
              Showing the {incidents.length} most recent of {total} incidents.
            </li>
          )}
        </ul>
      ))}
    </div>
  )
}

// ── formatting helpers ─────────────────────────────────────────────────────

function formatUptime(pct: number): string {
  // Trim to two decimals but drop trailing zeros (99.9 not 99.90, 100 not 100.00).
  return Number(pct.toFixed(2)).toString()
}

function uptimeColor(pct: number | null): string {
  if (pct == null) return 'var(--status-unknown)'
  if (pct >= 99) return 'var(--status-up)'
  if (pct >= 95) return 'var(--status-attention)'
  return 'var(--status-down)'
}

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m`
  const h = Math.floor(m / 60)
  const mm = m % 60
  if (h < 24) return mm ? `${h}h ${mm}m` : `${h}h`
  const d = Math.floor(h / 24)
  const hh = h % 24
  return hh ? `${d}d ${hh}h` : `${d}d`
}

function formatDateTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

// Compact label for the sparkline's time axis (start / end of the shown window).
function formatAxisTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}
