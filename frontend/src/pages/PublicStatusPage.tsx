import { useQuery } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import type { PublicStatusPage as PublicStatusPageData, PublicStatusService, ServiceStatus } from '@/lib/types'
import '@/styles/public-status.css'

/**
 * V10.2 — the public, unauthenticated status page rendered at `/status/{slug}`. Fetches the
 * whitelisted payload from `GET /api/status/{slug}` (no auth header, no token refresh) and shows,
 * per selected service, its live status, 24 h / 7 d / 30 d uptime % and a 30-day history bar —
 * plus an aggregate banner. An unpublished or unknown slug renders a plain "not found".
 */
export function PublicStatusPage() {
  const { slug = '' } = useParams<{ slug: string }>()

  const { data, isLoading, error } = useQuery<PublicStatusPageData>({
    queryKey: ['public-status', slug],
    queryFn: async () => {
      const resp = await fetch(`/api/status/${encodeURIComponent(slug)}`)
      if (!resp.ok) throw new Error(String(resp.status))
      return resp.json()
    },
    retry: false,
    refetchInterval: 60_000,
  })

  if (isLoading) {
    return (
      <div className="public-status">
        <div className="public-status-card public-status-loading">Loading status…</div>
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="public-status">
        <div className="public-status-card public-status-notfound">
          <h1>Status page not found</h1>
          <p>This status page doesn’t exist or isn’t published.</p>
        </div>
      </div>
    )
  }

  return (
    <div className="public-status">
      <div className="public-status-inner">
        <header className="public-status-header">
          <h1 className="public-status-title">{data.title}</h1>
          {data.description && <p className="public-status-desc">{data.description}</p>}
        </header>

        <div className="public-status-banner" data-overall={data.overallStatus}>
          <span className="public-status-banner-dot" />
          {overallLabel(data.overallStatus)}
        </div>

        <div className="public-status-services">
          {data.services.map((s, i) => (
            <ServiceRow key={i} service={s} />
          ))}
          {data.services.length === 0 && (
            <div className="public-status-card public-status-empty">No services are listed on this page.</div>
          )}
        </div>

        <footer className="public-status-footer">
          <span>Updated {formatDateTime(data.generatedUtc)}</span>
          <span>Powered by Stashboard</span>
        </footer>
      </div>
    </div>
  )
}

function ServiceRow({ service }: { service: PublicStatusService }) {
  const status = resolveStatus(service.status)
  return (
    <div className="public-status-card public-status-service">
      <div className="public-status-service-head">
        <span className="public-status-service-name">
          <span className="public-status-dot" data-status={status.toLowerCase()} />
          {service.name}
        </span>
        <span className="public-status-service-status" data-status={status.toLowerCase()}>
          {statusLabel(status)}
        </span>
      </div>

      <HistoryBar service={service} />

      <div className="public-status-uptime-row">
        <UptimeStat label="24h" value={service.uptime24h} />
        <UptimeStat label="7d" value={service.uptime7d} />
        <UptimeStat label="30d" value={service.uptime30d} />
      </div>
    </div>
  )
}

function HistoryBar({ service }: { service: PublicStatusService }) {
  if (service.history.length === 0) {
    return <p className="public-status-history-empty">No history recorded yet.</p>
  }
  return (
    <div className="public-status-history" role="img" aria-label="30-day uptime history">
      {service.history.map((b, i) => (
        <span
          key={i}
          className="public-status-history-bar"
          data-status={b.status}
          title={`${formatDate(b.dateUtc)} — ${b.uptime == null ? 'no data' : `${formatUptime(b.uptime)}% uptime`}`}
        />
      ))}
    </div>
  )
}

function UptimeStat({ label, value }: { label: string; value: number | null }) {
  return (
    <div className="public-status-uptime">
      <span className="public-status-uptime-label">{label}</span>
      <span className="public-status-uptime-value" style={{ color: uptimeColor(value) }}>
        {value == null ? '—' : `${formatUptime(value)}%`}
      </span>
    </div>
  )
}

// ── helpers ─────────────────────────────────────────────────────────────────

const resolveStatus = (s: ServiceStatus): string =>
  typeof s === 'number' ? (['Unknown', 'Up', 'Down', 'NeedsAttention'][s] ?? 'Unknown') : s

function statusLabel(status: string): string {
  if (status === 'Up') return 'Operational'
  if (status === 'Down') return 'Down'
  if (status === 'NeedsAttention') return 'Degraded'
  return 'Unknown'
}

function overallLabel(overall: string): string {
  switch (overall) {
    case 'operational':
      return 'All systems operational'
    case 'degraded':
      return 'Some systems degraded'
    case 'down':
      return 'Some systems are down'
    default:
      return 'Status unknown'
  }
}

function formatUptime(pct: number): string {
  return Number(pct.toFixed(2)).toString()
}

function uptimeColor(pct: number | null): string {
  if (pct == null) return 'var(--status-unknown)'
  if (pct >= 99) return 'var(--status-up)'
  if (pct >= 95) return 'var(--status-attention)'
  return 'var(--status-down)'
}

function formatDate(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

function formatDateTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}
