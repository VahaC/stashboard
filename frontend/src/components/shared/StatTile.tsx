// V6.8 — the metric tile + sparkline shared by the Proxmox LXC modal (Stats
// tab) and the PVE node modal (CPU/RAM + Network tabs). Extracted verbatim from
// the LXC modal so both surfaces render identical tiles instead of forking. The
// `docker-stats-*` styles it relies on come from service-modal.css, imported by
// whichever modal mounts it.

export interface SparkSeries {
  data: number[]
  color: string
  label: string
  format: (v: number) => string
}

export function StatTile({ label, valueText, series, max }: {
  label: string
  valueText: string
  series: SparkSeries[]
  max?: number
}) {
  const drawable = series.filter((s) => s.data.length > 1)
  return (
    <div className="docker-stats-tile">
      <div className="docker-stats-tile-head">
        <span className="docker-stats-tile-label">{label}</span>
      </div>
      <div className="docker-stats-tile-value">{valueText}</div>
      {drawable.length > 0 && (
        <>
          <Sparkline series={drawable} max={max} />
          <div className="docker-stats-series-summary">
            {series.map((s) => {
              const now = s.data.at(-1) ?? 0
              const peak = s.data.length ? Math.max(...s.data) : 0
              return (
                <div key={s.label} className="docker-stats-series-row">
                  <span className="docker-stats-series-name">
                    <span className="docker-stats-series-dot" style={{ backgroundColor: s.color }} aria-hidden />
                    {s.label}
                  </span>
                  <span className="docker-stats-series-metric">Now {s.format(now)}</span>
                  <span className="docker-stats-series-metric">Peak {s.format(peak)}</span>
                </div>
              )
            })}
          </div>
        </>
      )}
    </div>
  )
}

export function Sparkline({ series, max }: { series: SparkSeries[]; max?: number }) {
  const width = 240
  const height = 96
  const gridRows = 4
  const gridCols = 6
  const padTop = 6
  const padBottom = 6
  const graphH = height - padTop - padBottom
  const seriesMax = Math.max(max ?? 0, ...series.flatMap((s) => s.data), 1)
  const yScale = (v: number) => padTop + (1 - Math.max(0, Math.min(v, seriesMax)) / seriesMax) * graphH
  const yLabels = [seriesMax, seriesMax * 0.5, 0]

  return (
    <div className="docker-stats-sparkline-wrap">
      <svg className="docker-stats-sparkline" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" role="img" aria-hidden>
        <rect x="0" y="0" width={width} height={height} fill="var(--card)" />
        {Array.from({ length: gridRows + 1 }).map((_, i) => (
          <line key={`h-${i}`} x1="0" y1={(i / gridRows) * height} x2={width} y2={(i / gridRows) * height} className="docker-stats-grid-line" />
        ))}
        {Array.from({ length: gridCols + 1 }).map((_, i) => (
          <line key={`v-${i}`} x1={(i / gridCols) * width} y1="0" x2={(i / gridCols) * width} y2={height} className="docker-stats-grid-line docker-stats-grid-line-v" />
        ))}
        {series.map((s) => {
          const stepX = s.data.length > 1 ? width / (s.data.length - 1) : 0
          const line = s.data.map((v, i) => `${(i * stepX).toFixed(1)},${yScale(v).toFixed(1)}`).join(' ')
          const lastY = yScale(s.data.at(-1) ?? 0)
          return (
            <g key={s.label}>
              <polygon points={`0,${height} ${line} ${width},${height}`} fill={s.color} className="docker-stats-series-area" />
              <polyline points={line} fill="none" stroke={s.color} />
              <circle cx={width} cy={lastY} r="2.2" fill={s.color} />
            </g>
          )
        })}
      </svg>
      <div className="docker-stats-y-axis" aria-hidden>
        {yLabels.map((v, i) => <span key={`axis-${i}`}>{formatAxisValue(v)}</span>)}
      </div>
    </div>
  )
}

function formatAxisValue(value: number): string {
  if (value <= 0) return '0'
  if (value < 100) return `${value.toFixed(0)}`
  const units = ['k', 'M', 'G', 'T']
  let scaled = value
  let unit = -1
  while (scaled >= 1000 && unit < units.length - 1) { scaled /= 1000; unit += 1 }
  if (unit < 0) return value.toFixed(0)
  return `${scaled.toFixed(scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2)}${units[unit]}`
}
