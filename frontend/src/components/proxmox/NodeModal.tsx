import { Fragment, useEffect, useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  Activity,
  AlertCircle,
  Bell,
  Cpu,
  HardDrive,
  Info,
  Loader2,
  Network,
  Play,
  RefreshCw,
  Server,
  Square,
  SquareChevronRight,
  Thermometer,
} from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { StatTile } from '@/components/shared/StatTile'
import { StateBadge } from '@/components/shared/StateBadge'
import { NodeConsolePanel } from '@/components/proxmox/LxcConsolePanel'
import { NodeAlertsTab } from '@/components/proxmox/NodeAlertsTab'
import { useFeatures } from '@/lib/queries'
import {
  fetchProxmoxNodeStatus,
  proxmoxQk,
  useProxmoxNodeCpu,
  useProxmoxNodeDiskIo,
  useProxmoxNodeDisks,
  useProxmoxNodeDiskSelfTest,
  useProxmoxNodeDiskSmart,
  useProxmoxNodeInterfaces,
  useProxmoxNodeNetwork,
  useProxmoxNodeRrd,
  useProxmoxNodeSensors,
  useProxmoxNodeStatus,
  useProxmoxNodeStorage,
  useProxmoxNodeThinPools,
  type ProxmoxRrdTimeframe,
} from '@/lib/proxmox-queries'
import {
  THRESHOLDS,
  cpuPercent,
  diskLevel,
  healthAdvice,
  levelFor,
  memPercent,
  percent,
  rootPercent,
  tempLevel,
  type HealthLevel,
} from '@/lib/proxmox-node-health'
import type {
  ProxmoxConnection,
  ProxmoxDiskSelfTest,
  ProxmoxInterfaceStat,
  ProxmoxNodeDisk,
  ProxmoxNodeStorage,
  ProxmoxSensorReading,
  ProxmoxThinPool,
} from '@/lib/types'
import { cn, getApiErrorMessage } from '@/lib/utils'
import { useNowTick } from '@/lib/use-now-tick'
// Reuse the Docker container modal shell + stat tiles verbatim, exactly like the
// LXC modal — the node modal is the same surface, not a parallel one.
import '@/styles/docker-instances.css'  // .container-modal-* shell
import '@/styles/service-modal.css'     // .docker-stats-* tiles + sparkline
import '@/styles/proxmox.css'           // .pve-* node-specific bits

/**
 * V6.8 — the PVE node card's detailed diagnostics modal. Opened from the node
 * card on the Proxmox page; reuses the Docker/LXC `container-modal-*` shell and
 * the shared {@link StatTile} so the cross-platform UI stays consistent.
 *
 * Tabs: Overview · CPU/RAM · Storage/SMART · Network · Sensors · Alerts. Each
 * tab loads its data lazily (only when first opened), mirroring the LXC modal.
 */
export type NodeModalTab = 'overview' | 'cpuram' | 'storage' | 'network' | 'sensors' | 'alerts' | 'console'

interface NodeModalProps {
  connection: ProxmoxConnection
  initialTab?: NodeModalTab
  onClose: () => void
}

const TABS: ReadonlyArray<{ id: NodeModalTab; label: string; icon: typeof Info }> = [
  { id: 'overview', label: 'Overview', icon: Info },
  { id: 'cpuram', label: 'CPU / RAM', icon: Cpu },
  { id: 'storage', label: 'Storage / SMART', icon: HardDrive },
  { id: 'network', label: 'Network', icon: Network },
  { id: 'sensors', label: 'Sensors', icon: Thermometer },
  { id: 'alerts', label: 'Alerts', icon: Bell },
  { id: 'console', label: 'Console', icon: SquareChevronRight },
]

export function NodeModal({ connection, initialTab = 'overview', onClose }: NodeModalProps) {
  const [tab, setTab] = useState<NodeModalTab>(initialTab)
  const poll = connection.telemetryPollSeconds
  // Shares the Overview tab's cached query (same key) — no extra request — just
  // to badge online/offline in the header with the shared StateBadge.
  const status = useProxmoxNodeStatus(connection.id, true, poll)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <Server className="h-4 w-4" />
            <span>{connection.nodeName}</span>
            <span className="docker-section-badge" data-status="Node">node</span>
            {(status.data || status.isError) && (
              <StateBadge state={status.data ? 'online' : 'offline'} size="sm" />
            )}
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            {connection.name} · {connection.apiBaseUrl}
          </DialogDescription>
        </DialogHeader>

        <nav className="container-modal-tabs" role="tablist">
          {TABS.map(({ id, label, icon: Icon }) => (
            <button
              key={id}
              type="button"
              role="tab"
              aria-selected={tab === id}
              className={cn('container-modal-tab', tab === id && 'container-modal-tab-active')}
              onClick={() => setTab(id)}
            >
              <Icon className="h-3.5 w-3.5" /> {label}
            </button>
          ))}
        </nav>

        <div className="container-modal-body" role="tabpanel">
          {tab === 'overview' && <OverviewTab connection={connection} />}
          {tab === 'cpuram' && <CpuRamTab connectionId={connection.id} pollSeconds={poll} />}
          {tab === 'storage' && <StorageTab connectionId={connection.id} pollSeconds={poll} />}
          {tab === 'network' && <NetworkTab connectionId={connection.id} pollSeconds={poll} />}
          {tab === 'sensors' && <SensorsTab connectionId={connection.id} pollSeconds={poll} />}
          {tab === 'alerts' && <NodeAlertsTab connection={connection} />}
          {tab === 'console' && <ConsoleTab connection={connection} />}
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ── Overview tab ──────────────────────────────────────────────────────────────

function OverviewTab({ connection }: { connection: ProxmoxConnection }) {
  const query = useProxmoxNodeStatus(connection.id, true, connection.telemetryPollSeconds)
  // V6.8.2 — steal % + MemAvailable come from the SSH collector (not the API);
  // best-effort, so the Overview still renders fully when SSH is absent.
  const cpu = useProxmoxNodeCpu(connection.id, true, connection.telemetryPollSeconds)

  if (query.isLoading) return <p className="container-modal-empty">Loading node status from Proxmox…</p>
  if (query.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read node status'}
      </p>
    )
  }
  const s = query.data
  if (!s) return <p className="container-modal-empty">No status.</p>

  const cpuPct = cpuPercent(s)
  const memPct = memPercent(s)
  const rootPct = rootPercent(s)
  const threads = s.cpus != null && s.sockets != null && s.cores != null
    ? `${s.sockets} socket${s.sockets === 1 ? '' : 's'} · ${s.cores} cores · ${s.cpus} threads`
    : s.cpus != null ? `${s.cpus} threads` : null

  return (
    <div className="container-modal-overview">
      <RefreshedAt at={query.dataUpdatedAt} />

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Node</h3>
        <dl className="container-modal-summary">
          <dt>Node</dt><dd>{connection.nodeName}</dd>
          <dt>Host</dt><dd>{connection.name}</dd>
          {s.uptimeSeconds != null && (<><dt>Uptime</dt><dd>{formatUptime(s.uptimeSeconds) ?? '—'}</dd></>)}
          {s.kernelVersion && (<><dt>Kernel</dt><dd><code className="container-modal-code">{s.kernelVersion}</code></dd></>)}
          {s.pveVersion && (<><dt>PVE version</dt><dd><code className="container-modal-code">{s.pveVersion}</code></dd></>)}
          {s.subscriptionStatus && (
            <><dt>Subscription</dt><dd>{subscriptionLabel(s.subscriptionStatus, s.subscriptionLevel)}</dd></>
          )}
        </dl>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">CPU</h3>
        <dl className="container-modal-summary">
          {s.cpuModel && (<><dt>Model</dt><dd>{s.cpuModel}</dd></>)}
          {threads && (<><dt>Topology</dt><dd>{threads}</dd></>)}
          {s.cpuMhz != null && (<><dt>Frequency</dt><dd>{Math.round(s.cpuMhz)} MHz</dd></>)}
          {s.hvm != null && (<><dt>Virtualization</dt><dd>{s.hvm ? 'VT-x / AMD-V available' : 'not available'}</dd></>)}
          {cpuPct != null && (<><dt>CPU load</dt><dd><MeterValue pct={cpuPct} level={levelFor(cpuPct, THRESHOLDS.cpu)} text={`${cpuPct.toFixed(1)}%`} advice={healthAdvice('cpu', levelFor(cpuPct, THRESHOLDS.cpu))} /></dd></>)}
          {(s.load1 != null || s.load5 != null || s.load15 != null) && (
            <><dt>Load avg</dt><dd>{[s.load1, s.load5, s.load15].map((l) => l?.toFixed(2) ?? '—').join(' / ')}</dd></>
          )}
          {s.ioWaitFraction != null && (<><dt>IO wait</dt><dd>{(s.ioWaitFraction * 100).toFixed(1)}%</dd></>)}
          {cpu.data?.available && cpu.data.stealPercent != null && (
            <><dt>CPU steal</dt><dd title={cpu.data.stealPercent >= 5
              ? 'The hypervisor is withholding CPU time from this node (it is itself a guest / over-committed host). Suggested action: reduce contention on the underlying host.'
              : 'Share of CPU time stolen by the hypervisor (≈0 on bare metal).'}>
              {cpu.data.stealPercent.toFixed(1)}%{cpu.data.stealPercent >= 5 ? ' ⚠' : ''}
            </dd></>
          )}
        </dl>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Memory</h3>
        <dl className="container-modal-summary">
          {memPct != null && (
            <><dt>RAM</dt><dd><MeterValue pct={memPct} level={levelFor(memPct, THRESHOLDS.mem)}
              text={`${formatBytes(s.memUsed) ?? '—'} / ${formatBytes(s.memTotal) ?? '—'} (${memPct.toFixed(0)}%)`}
              advice={healthAdvice('mem', levelFor(memPct, THRESHOLDS.mem))} /></dd></>
          )}
          {s.memFree != null && (<><dt>Free</dt><dd>{formatBytes(s.memFree) ?? '—'}</dd></>)}
          {cpu.data?.available && cpu.data.memAvailableBytes != null && (
            <><dt>Available</dt><dd title="MemAvailable from /proc/meminfo over SSH — what's actually reclaimable for new allocations (the API reports only free).">
              {formatBytes(cpu.data.memAvailableBytes) ?? '—'}
            </dd></>
          )}
          {s.swapTotal != null && s.swapTotal > 0 && (
            <><dt>Swap</dt><dd>{formatBytes(s.swapUsed) ?? '0 B'} / {formatBytes(s.swapTotal)}</dd></>
          )}
        </dl>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Host storage</h3>
        <dl className="container-modal-summary">
          {rootPct != null ? (
            <><dt>Root FS</dt><dd><MeterValue pct={rootPct} level={levelFor(rootPct, THRESHOLDS.root)}
              text={`${formatBytes(s.rootUsed) ?? '—'} / ${formatBytes(s.rootTotal) ?? '—'} (${rootPct.toFixed(0)}%)`}
              advice={healthAdvice('root', levelFor(rootPct, THRESHOLDS.root))} /></dd></>
          ) : (
            <><dt>Root FS</dt><dd className="container-modal-empty">not available</dd></>
          )}
        </dl>
      </section>
    </div>
  )
}

// ── CPU / RAM tab (history sparklines) ────────────────────────────────────────

const CPU_COLOR = '#22c55e'
const LOAD_COLOR = '#8b5cf6'
const MEM_COLOR = '#3b82f6'
const SWAP_COLOR = '#f59e0b'
const NET_IN_COLOR = '#22c55e'
const NET_OUT_COLOR = '#f59e0b'

const TIMEFRAMES: ReadonlyArray<{ id: ProxmoxRrdTimeframe; label: string }> = [
  { id: 'hour', label: 'Hour' },
  { id: 'day', label: 'Day' },
  { id: 'week', label: 'Week' },
]

function CpuRamTab({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  // Mirror the LXC modal's Stats tab: Live (real-time poll) by default, with a
  // History toggle for the RRD series.
  const [mode, setMode] = useState<'live' | 'history'>('live')
  return (
    <div className="docker-stats-body">
      <PerCoreSection connectionId={connectionId} pollSeconds={pollSeconds} />
      <div className="container-modal-stats-header">
        <div className="container-modal-actions">
          <Button type="button" size="sm" variant={mode === 'live' ? 'default' : 'outline'} onClick={() => setMode('live')}>Live</Button>
          <Button type="button" size="sm" variant={mode === 'history' ? 'default' : 'outline'} onClick={() => setMode('history')}>History</Button>
        </div>
      </div>
      {mode === 'live'
        ? <LiveCpuRam connectionId={connectionId} />
        : <HistoryCpuRam connectionId={connectionId} pollSeconds={pollSeconds} />}
    </div>
  )
}

// V6.8.2 — per-core utilisation bars from the SSH /proc/stat collector. Degrades
// to a "not available" note when SSH isn't configured.
function PerCoreSection({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  const cpu = useProxmoxNodeCpu(connectionId, true, pollSeconds)
  const data = cpu.data

  return (
    <section className="container-modal-section">
      <div className="container-modal-stats-header pve-section-header">
        <h3 className="container-modal-section-title">Per-core utilisation</h3>
        {data?.available && data.stealPercent != null && data.stealPercent >= 1 && (
          <span className="pve-row-sub" title="Hypervisor-stolen CPU time across all cores.">
            steal {data.stealPercent.toFixed(1)}%
          </span>
        )}
      </div>
      {cpu.isLoading && <p className="container-modal-empty">Reading /proc/stat over SSH…</p>}
      {!cpu.isLoading && !data?.available && (
        <p className="container-modal-empty">
          {data?.error ?? 'Per-core CPU is not available.'} Add SSH credentials to this host to read{' '}
          <code>/proc/stat</code> for per-core utilisation + steal.
        </p>
      )}
      {data?.available && data.cores.length > 0 && (
        <div className="pve-core-grid">
          {data.cores.map((c) => {
            const level = levelFor(c.utilPercent, THRESHOLDS.cpu)
            return (
              <div className="pve-core" key={c.core} title={`core ${c.core}: ${c.utilPercent.toFixed(0)}%${c.stealPercent >= 1 ? ` · steal ${c.stealPercent.toFixed(1)}%` : ''}`}>
                <span className="pve-core-label">#{c.core}</span>
                <Meter pct={c.utilPercent} level={level} />
                <span className="pve-core-pct">{c.utilPercent.toFixed(0)}%</span>
              </div>
            )
          })}
        </div>
      )}
    </section>
  )
}

// Real-time view — Proxmox has no node stats stream, so poll status every 2s and
// keep a rolling window, exactly like the LXC modal's LiveStats.
const LIVE_WINDOW = 120
const LIVE_INTERVAL_MS = 2000

interface LiveSample { cpu: number; load: number; mem: number; memMax: number; swap: number }

function LiveCpuRam({ connectionId }: { connectionId: string }) {
  const [samples, setSamples] = useState<LiveSample[]>([])
  const [error, setError] = useState<string | null>(null)
  const [paused, setPaused] = useState(false)

  useEffect(() => {
    if (paused) return
    let cancelled = false
    const poll = async () => {
      try {
        const s = await fetchProxmoxNodeStatus(connectionId)
        if (cancelled) return
        setError(null)
        setSamples((prev) => {
          const next = [...prev, {
            cpu: (s.cpuFraction ?? 0) * 100,
            load: s.load1 ?? 0,
            mem: s.memUsed ?? 0,
            memMax: s.memTotal ?? 0,
            swap: s.swapUsed ?? 0,
          }]
          return next.length > LIVE_WINDOW ? next.slice(next.length - LIVE_WINDOW) : next
        })
      } catch (e) {
        if (!cancelled) setError(getApiErrorMessage(e) ?? 'Failed to read node status')
      }
    }
    void poll()
    const id = window.setInterval(() => void poll(), LIVE_INTERVAL_MS)
    return () => { cancelled = true; window.clearInterval(id) }
  }, [connectionId, paused])

  const cpu = useMemo(() => samples.map((s) => s.cpu), [samples])
  const load = useMemo(() => samples.map((s) => s.load), [samples])
  const mem = useMemo(() => samples.map((s) => s.mem), [samples])
  const swap = useMemo(() => samples.map((s) => s.swap), [samples])
  const memMax = samples.length ? samples[samples.length - 1].memMax : 0

  return (
    <>
      <div className="container-modal-stats-header">
        <span className="container-modal-empty">
          {paused ? 'paused' : 'live'} · {samples.length} sample{samples.length === 1 ? '' : 's'} · polled every {LIVE_INTERVAL_MS / 1000}s
        </span>
        <Button type="button" size="sm" variant="outline" onClick={() => setPaused((p) => !p)}>
          {paused ? <><Play className="h-3.5 w-3.5" /> Resume</> : <><Square className="h-3.5 w-3.5" /> Pause</>}
        </Button>
      </div>
      {error && <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {error}</p>}
      {samples.length === 0 && !error && <p className="container-modal-empty">Waiting for the first sample…</p>}
      {samples.length > 0 && (
        <div className="docker-stats-grid">
          <StatTile label="CPU" valueText={`${(cpu.at(-1) ?? 0).toFixed(1)}%`} max={100}
            series={[{ data: cpu, color: CPU_COLOR, label: 'CPU', format: (v) => `${v.toFixed(1)}%` }]} />
          <StatTile label="Load average (1m)" valueText={(load.at(-1) ?? 0).toFixed(2)}
            series={[{ data: load, color: LOAD_COLOR, label: 'Load', format: (v) => v.toFixed(2) }]} />
          <StatTile label="Memory"
            valueText={`${formatBytes(mem.at(-1) ?? 0) ?? '0 B'}${memMax ? ` / ${formatBytes(memMax)}` : ''}`}
            max={memMax || undefined}
            series={[{ data: mem, color: MEM_COLOR, label: 'Used', format: (v) => formatBytes(v) ?? '0 B' }]} />
          <StatTile label="Swap" valueText={formatBytes(swap.at(-1) ?? 0) ?? '0 B'}
            series={[{ data: swap, color: SWAP_COLOR, label: 'Swap', format: (v) => formatBytes(v) ?? '0 B' }]} />
        </div>
      )}
    </>
  )
}

function HistoryCpuRam({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  const [timeframe, setTimeframe] = useState<ProxmoxRrdTimeframe>('hour')
  const query = useProxmoxNodeRrd(connectionId, timeframe, true, pollSeconds)
  const points = useMemo(() => query.data ?? [], [query.data])

  const series = useMemo(() => ({
    cpu: points.map((p) => (p.cpu ?? 0) * 100),
    load: points.map((p) => p.loadAvg ?? 0),
    mem: points.map((p) => p.memUsed ?? 0),
    swap: points.map((p) => p.swapUsed ?? 0),
  }), [points])
  const memMax = points.length ? (points[points.length - 1].memTotal ?? 0) : 0

  return (
    <>
      <TimeframeBar timeframe={timeframe} onChange={setTimeframe} fetching={query.isFetching} />
      {query.isLoading && <p className="container-modal-empty">Loading metrics from Proxmox…</p>}
      {query.error && (
        <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read metrics'}</p>
      )}
      {!query.isLoading && !query.error && points.length === 0 && (
        <p className="container-modal-empty">No metrics for this timeframe.</p>
      )}
      {points.length > 0 && (
        <div className="docker-stats-grid">
          <StatTile label="CPU" valueText={`${(series.cpu.at(-1) ?? 0).toFixed(1)}%`} max={100}
            series={[{ data: series.cpu, color: CPU_COLOR, label: 'CPU', format: (v) => `${v.toFixed(1)}%` }]} />
          <StatTile label="Load average" valueText={(series.load.at(-1) ?? 0).toFixed(2)}
            series={[{ data: series.load, color: LOAD_COLOR, label: 'Load', format: (v) => v.toFixed(2) }]} />
          <StatTile label="Memory"
            valueText={`${formatBytes(series.mem.at(-1) ?? 0) ?? '0 B'}${memMax ? ` / ${formatBytes(memMax)}` : ''}`}
            max={memMax || undefined}
            series={[{ data: series.mem, color: MEM_COLOR, label: 'Used', format: (v) => formatBytes(v) ?? '0 B' }]} />
          <StatTile label="Swap" valueText={formatBytes(series.swap.at(-1) ?? 0) ?? '0 B'}
            series={[{ data: series.swap, color: SWAP_COLOR, label: 'Swap', format: (v) => formatBytes(v) ?? '0 B' }]} />
        </div>
      )}
    </>
  )
}

// ── Storage / SMART tab ───────────────────────────────────────────────────────

function StorageTab({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  const storage = useProxmoxNodeStorage(connectionId)
  const disks = useProxmoxNodeDisks(connectionId)
  const thinPools = useProxmoxNodeThinPools(connectionId)
  const diskIo = useProxmoxNodeDiskIo(connectionId, true, pollSeconds)
  const qc = useQueryClient()
  const now = useNowTick()

  // SMART is cached for an hour (it spins physical disks); "Refresh now"
  // invalidates the disk list + every expanded per-disk SMART query so the next
  // read is immediate, bypassing the cache.
  const refreshSmart = () => {
    void qc.invalidateQueries({ queryKey: proxmoxQk.nodeDisks(connectionId) })
  }

  return (
    <div className="container-modal-overview">
      {storage.dataUpdatedAt > 0 && <RefreshedAt at={storage.dataUpdatedAt} />}
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Storage pools</h3>
        {storage.isLoading && <p className="container-modal-empty">Loading storage…</p>}
        {storage.error && (
          <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(storage.error) ?? 'Failed to read storage'}</p>
        )}
        {storage.data?.length === 0 && <p className="container-modal-empty">No storage pools reported.</p>}
        {storage.data && storage.data.length > 0 && (
          <div className="pve-rows pve-rows-2col">
            {storage.data.map((p) => <StoragePoolRow key={p.storage} pool={p} />)}
          </div>
        )}
      </section>

      {/* V6.8.2 — LVM-thin pool fill warnings (only when the host has thin pools). */}
      {thinPools.data?.available && thinPools.data.pools.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Thin pools</h3>
          <div className="pve-rows pve-rows-2col">
            {thinPools.data.pools.map((p) => <ThinPoolRow key={`${p.volumeGroup}/${p.name}`} pool={p} />)}
          </div>
        </section>
      )}

      {/* V6.8.2 — live per-disk IO (read/write throughput, IOPS, latency) over SSH. */}
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Disk IO</h3>
        {diskIo.isLoading && <p className="container-modal-empty">Reading /proc/diskstats over SSH…</p>}
        {!diskIo.isLoading && !diskIo.data?.available && (
          <p className="container-modal-empty">
            {diskIo.data?.error ?? 'Disk IO is not available.'} Add SSH credentials to this host to sample{' '}
            <code>/proc/diskstats</code> for throughput, IOPS, and latency.
          </p>
        )}
        {diskIo.data?.available && diskIo.data.disks.length > 0 && (
          <table className="pve-io-table">
            <thead>
              <tr><th>Disk</th><th>Read</th><th>Write</th><th>r/s</th><th>w/s</th><th>r await</th><th>w await</th></tr>
            </thead>
            <tbody>
              {diskIo.data.disks.map((d) => (
                <tr key={d.device}>
                  <td><code style={{ fontFamily: 'var(--font-mono)' }}>{d.device}</code></td>
                  <td>{formatRate(d.readBytesPerSec)}</td>
                  <td>{formatRate(d.writeBytesPerSec)}</td>
                  <td>{d.readIops.toFixed(0)}</td>
                  <td>{d.writeIops.toFixed(0)}</td>
                  <td>{d.readAwaitMs != null ? `${d.readAwaitMs.toFixed(1)} ms` : '—'}</td>
                  <td>{d.writeAwaitMs != null ? `${d.writeAwaitMs.toFixed(1)} ms` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="container-modal-section">
        <div className="container-modal-stats-header pve-section-header">
          <h3 className="container-modal-section-title">Disks &amp; SMART</h3>
          <div className="container-modal-actions">
            {disks.dataUpdatedAt > 0 && (
              <span className="container-modal-empty" title="SMART is read from the physical disks at most once per hour">
                read {relativeTime(disks.dataUpdatedAt, now)}
              </span>
            )}
            <Button type="button" size="sm" variant="outline" disabled={disks.isFetching} onClick={refreshSmart}>
              <RefreshCw className={cn('h-3.5 w-3.5', disks.isFetching && 'animate-spin')} /> Refresh now
            </Button>
          </div>
        </div>
        {disks.isLoading && <p className="container-modal-empty">Loading disks…</p>}
        {disks.error && (
          <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(disks.error) ?? 'Failed to read disks'}</p>
        )}
        {disks.data?.length === 0 && <p className="container-modal-empty">No physical disks reported.</p>}
        {disks.data && disks.data.length > 0 && (
          <div className="pve-rows-2col pve-rows-2col-start">
            {disks.data.map((d) => <DiskRow key={d.devPath} connectionId={connectionId} disk={d} />)}
          </div>
        )}
      </section>
    </div>
  )
}

function StoragePoolRow({ pool }: { pool: ProxmoxNodeStorage }) {
  const pct = percent(pool.used, pool.total)
  const level = levelFor(pct, THRESHOLDS.root)
  const advice = healthAdvice('storage', level)
  return (
    <div className="pve-row" title={advice}>
      <div className="pve-row-head">
        <span className="pve-row-name" title={pool.storage}>{pool.storage}</span>
        <span className="pve-row-sub">{pool.type ?? '—'}{!pool.active && ' · inactive'}</span>
      </div>
      <Meter pct={pct} level={level} />
      <div className="pve-row-foot">
        {pct != null
          ? `${formatBytes(pool.used) ?? '—'} / ${formatBytes(pool.total) ?? '—'} (${pct.toFixed(0)}%)`
          : 'usage not available'}
      </div>
    </div>
  )
}

function DiskRow({ connectionId, disk }: { connectionId: string; disk: ProxmoxNodeDisk }) {
  const [open, setOpen] = useState(false)
  const smart = useProxmoxNodeDiskSmart(connectionId, open ? disk.devPath : null)
  // V6.8.2 — last self-test + critical counters over SSH, loaded on expand.
  const selfTest = useProxmoxNodeDiskSelfTest(connectionId, open ? disk.devPath : null)
  const level = diskLevel(disk.health, disk.wearoutPercent)

  return (
    <details className="container-modal-collapsible" open={open} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="container-modal-collapsible-with-action">
        <span className="pve-disk-name">
          <code style={{ fontFamily: 'var(--font-mono)' }}>{disk.devPath}</code>
          <span className="pve-row-sub">
            {[disk.type?.toUpperCase(), formatBytes(disk.size), disk.model].filter(Boolean).join(' · ')}
          </span>
        </span>
        <span className="pve-disk-meta">
          {disk.wearoutPercent != null && <span className="pve-row-sub">wearout {disk.wearoutPercent}%</span>}
          <HealthBadge level={level} text={disk.health ?? 'unknown'} advice={healthAdvice('disk', level)} />
        </span>
      </summary>
      {open && (
        <>
          <SelfTestPanel data={selfTest.data} loading={selfTest.isLoading} />
          {smart.isLoading && <p className="container-modal-empty">Loading SMART…</p>}
          {smart.error && (
            <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(smart.error) ?? 'Failed to read SMART'}</p>
          )}
          {smart.data && (
            smart.data.attributes.length > 0 ? (
              <table className="pve-smart-table">
                <thead>
                  <tr><th>ID</th><th>Attribute</th><th>Value</th><th>Worst</th><th>Thresh</th><th>Raw</th></tr>
                </thead>
                <tbody>
                  {smart.data.attributes.map((a) => (
                    <tr key={a.id}>
                      <td>{a.id}</td>
                      <td>{a.name}</td>
                      <td>{a.value ?? '—'}</td>
                      <td>{a.worst ?? '—'}</td>
                      <td>{a.threshold ?? '—'}</td>
                      <td>{a.raw ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : smart.data.text ? (
              <pre className="container-modal-raw">{smart.data.text}</pre>
            ) : (
              <p className="container-modal-empty">No SMART attributes reported for this disk.</p>
            )
          )}
        </>
      )}
    </details>
  )
}

// V6.8.2 — last SMART self-test + the badged critical counters, lifted out of the
// raw attribute table so a degrading drive is obvious at a glance.
function SelfTestPanel({ data, loading }: { data: ProxmoxDiskSelfTest | undefined; loading: boolean }) {
  if (loading) return <p className="container-modal-empty">Reading self-test log over SSH…</p>
  if (!data) return null
  if (!data.available) {
    return <p className="container-modal-empty">{data.error ?? 'Self-test data is not available for this disk.'}</p>
  }

  const ageHours = data.powerOnHours != null && data.lastTestPowerOnHours != null
    ? data.powerOnHours - data.lastTestPowerOnHours
    : null
  const testOk = data.lastTestStatus == null || /completed without error/i.test(data.lastTestStatus)

  const counters: ReadonlyArray<{ label: string; value: number | null }> = [
    { label: 'Reallocated', value: data.reallocatedSectors },
    { label: 'Pending', value: data.pendingSectors },
    { label: 'Uncorrectable', value: data.uncorrectableSectors },
    { label: 'Power-on hrs', value: data.powerOnHours },
  ]

  return (
    <div className="pve-selftest">
      <div className="pve-selftest-row">
        <span className="pve-row-sub">Last self-test</span>
        {data.lastTestStatus ? (
          <HealthBadge
            level={testOk ? 'ok' : 'crit'}
            text={`${data.lastTestType ?? 'test'}: ${data.lastTestStatus}`}
            advice={testOk ? undefined : 'The most recent SMART self-test did not complete cleanly. Suggested action: back up this drive and run an extended self-test / plan replacement.'}
          />
        ) : (
          <span className="pve-row-sub">never run</span>
        )}
        {ageHours != null && <span className="pve-row-sub">{ageHours <= 0 ? 'just now' : `${ageHours} h ago`}</span>}
      </div>
      <div className="pve-selftest-counters">
        {counters.map((c) => {
          // Power-on hours is informational; the fault counters badge red when >0.
          const isFault = c.label !== 'Power-on hrs'
          const bad = isFault && (c.value ?? 0) > 0
          return (
            <span key={c.label} className="pve-counter" data-level={bad ? 'crit' : 'ok'}>
              <span className="pve-counter-label">{c.label}</span>
              <span className="pve-counter-value">{c.value ?? '—'}</span>
            </span>
          )
        })}
      </div>
    </div>
  )
}

function ThinPoolRow({ pool }: { pool: ProxmoxThinPool }) {
  // A thin pool nearing 100% data — or, worse, 100% metadata (which wedges it) —
  // is the warning the spec calls for; classify on the worse of the two.
  const dataLevel = levelFor(pool.dataPercent, THRESHOLDS.root)
  const metaLevel = levelFor(pool.metadataPercent, THRESHOLDS.root)
  const level = dataLevel === 'crit' || metaLevel === 'crit' ? 'crit'
    : dataLevel === 'warn' || metaLevel === 'warn' ? 'warn' : 'ok'
  return (
    <div className="pve-row" title={healthAdvice('storage', level)}>
      <div className="pve-row-head">
        <span className="pve-row-name" title={`${pool.volumeGroup}/${pool.name}`}>{pool.name}</span>
        <span className="pve-row-sub">{pool.volumeGroup}{pool.sizeBytes != null && ` · ${formatBytes(pool.sizeBytes)}`}</span>
      </div>
      <Meter pct={pool.dataPercent} level={level} />
      <div className="pve-row-foot">
        {pool.dataPercent != null ? `data ${pool.dataPercent.toFixed(1)}%` : 'data —'}
        {pool.metadataPercent != null && ` · meta ${pool.metadataPercent.toFixed(1)}%`}
      </div>
    </div>
  )
}

// ── Network tab ───────────────────────────────────────────────────────────────

function NetworkTab({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  const rrd = useProxmoxNodeRrd(connectionId, 'hour', true, pollSeconds)
  const ifaces = useProxmoxNodeNetwork(connectionId)
  // V6.8.2 — live per-interface throughput + errors + link over SSH; replaces the
  // node-aggregate-only view as the primary signal (the RRD trend stays as history).
  const stats = useProxmoxNodeInterfaces(connectionId, true, pollSeconds)

  const points = useMemo(() => rrd.data ?? [], [rrd.data])
  const netIn = useMemo(() => points.map((p) => p.netIn ?? 0), [points])
  const netOut = useMemo(() => points.map((p) => p.netOut ?? 0), [points])

  // Index live stats by interface so the configured-interface rows can pick up
  // their rate / errors / link.
  const liveByIface = useMemo(() => {
    const map = new Map<string, ProxmoxInterfaceStat>()
    for (const s of stats.data?.interfaces ?? []) map.set(s.iface, s)
    return map
  }, [stats.data])

  return (
    <div className="container-modal-overview">
      {ifaces.dataUpdatedAt > 0 && <RefreshedAt at={ifaces.dataUpdatedAt} />}
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Throughput (last hour)</h3>
        {points.length > 0 ? (
          <div className="docker-stats-grid">
            <StatTile label="Network ↓ / ↑"
              valueText={`${formatRate(netIn.at(-1))} / ${formatRate(netOut.at(-1))}`}
              series={[
                { data: netIn, color: NET_IN_COLOR, label: 'In', format: formatRate },
                { data: netOut, color: NET_OUT_COLOR, label: 'Out', format: formatRate },
              ]} />
          </div>
        ) : (
          <p className="container-modal-empty">No throughput data yet.</p>
        )}
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Interfaces</h3>
        {ifaces.isLoading && <p className="container-modal-empty">Loading interfaces…</p>}
        {ifaces.error && (
          <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(ifaces.error) ?? 'Failed to read interfaces'}</p>
        )}
        {ifaces.data?.length === 0 && <p className="container-modal-empty">No interfaces reported.</p>}
        {ifaces.data && ifaces.data.length > 0 && (
          <div className="pve-rows">
            {ifaces.data.map((n) => {
              const live = liveByIface.get(n.iface)
              const errs = live ? live.rxErrors + live.txErrors : 0
              const link = live && (live.speedMbps != null || live.duplex || live.operState)
                ? [
                    live.speedMbps != null ? `${live.speedMbps >= 1000 ? `${live.speedMbps / 1000} Gb/s` : `${live.speedMbps} Mb/s`}` : null,
                    live.duplex ? `${live.duplex} duplex` : null,
                  ].filter(Boolean).join(' · ')
                : null
              return (
                <div className="pve-row" key={n.iface}>
                  <div className="pve-row-head">
                    <span className="pve-row-name">
                      <code style={{ fontFamily: 'var(--font-mono)' }}>{n.iface}</code>
                      <HealthBadge level={n.active ? 'ok' : 'warn'} text={n.active ? 'up' : 'down'} />
                      {errs > 0 && (
                        <HealthBadge level="warn" text={`${errs} err`}
                          advice="This interface is reporting RX/TX errors. Suggested action: check the cable/SFP, switch port, and driver." />
                      )}
                    </span>
                    <span className="pve-row-sub">{n.type ?? '—'}{n.method ? ` · ${n.method}` : ''}{link ? ` · ${link}` : ''}</span>
                  </div>
                  {live && (
                    <div className="pve-row-foot pve-iface-rates">
                      <span>↓ {formatRate(live.rxBytesPerSec)} · ↑ {formatRate(live.txBytesPerSec)}</span>
                      {(live.rxDropped > 0 || live.txDropped > 0) && (
                        <span className="pve-row-sub">drops {live.rxDropped + live.txDropped}</span>
                      )}
                    </div>
                  )}
                  <div className="pve-row-foot">
                    {[
                      n.cidr ?? n.address,
                      n.gateway ? `gw ${n.gateway}` : null,
                      n.bridgePorts ? `ports ${n.bridgePorts}` : null,
                      n.bondSlaves ? `slaves ${n.bondSlaves}` : null,
                    ].filter(Boolean).join(' · ') || '—'}
                  </div>
                </div>
              )
            })}
          </div>
        )}
        {!stats.isLoading && !stats.data?.available && (
          <p className="container-modal-empty">
            {stats.data?.error ?? 'Live per-interface throughput is not available.'} Add SSH credentials to read{' '}
            <code>/proc/net/dev</code> for per-interface rates, errors, and link speed.
          </p>
        )}
      </section>
    </div>
  )
}

// ── Sensors tab ───────────────────────────────────────────────────────────────

function SensorsTab({ connectionId, pollSeconds }: { connectionId: string; pollSeconds: number | null }) {
  const query = useProxmoxNodeSensors(connectionId, true, pollSeconds)

  if (query.isLoading) return <p className="container-modal-empty">Reading sensors over SSH…</p>
  if (query.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read sensors'}
      </p>
    )
  }
  const data = query.data
  if (!data) return null
  if (!data.available) {
    return (
      <div className="container-modal-overview">
        <RefreshedAt at={query.dataUpdatedAt} />
        <p className="container-modal-empty">
          {data.error ?? 'Sensor data is not available on this host.'}
        </p>
        <p className="container-modal-empty">
          CPU/board temperatures and fan speeds come from <code>lm-sensors</code> over SSH (the Proxmox API doesn't
          expose them). Install it on the host (<code>apt install lm-sensors &amp;&amp; sensors-detect</code>) and add
          SSH credentials to this host to enable this tab.
        </p>
      </div>
    )
  }

  const temps = data.readings.filter((r) => r.tempC != null)
  const fans = data.readings.filter((r) => r.rpm != null)
  // V6.8.2 — voltage rails (in*) and power inputs (power*) from the same sensors -j.
  const volts = data.readings.filter((r) => r.volts != null)
  const watts = data.readings.filter((r) => r.watts != null)

  return (
    <div className="container-modal-overview">
      <RefreshedAt at={query.dataUpdatedAt} />
      {temps.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Temperatures</h3>
          <div className="pve-rows pve-rows-2col">
            {temps.map((r, i) => <SensorTempRow key={`${r.chip}-${r.label}-${i}`} reading={r} />)}
          </div>
        </section>
      )}
      {fans.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Fans</h3>
          <dl className="container-modal-summary">
            {fans.map((r, i) => (
              <Fragment key={`${r.chip}-${r.label}-${i}`}>
                <dt>{r.label}</dt>
                <dd>{Math.round(r.rpm ?? 0)} RPM</dd>
              </Fragment>
            ))}
          </dl>
        </section>
      )}
      {volts.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Voltages</h3>
          <dl className="container-modal-summary">
            {volts.map((r, i) => (
              <Fragment key={`${r.chip}-${r.label}-${i}`}>
                <dt title={r.chip}>{r.label}</dt>
                <dd>{(r.volts ?? 0).toFixed(2)} V</dd>
              </Fragment>
            ))}
          </dl>
        </section>
      )}
      {watts.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Power</h3>
          <dl className="container-modal-summary">
            {watts.map((r, i) => (
              <Fragment key={`${r.chip}-${r.label}-${i}`}>
                <dt title={r.chip}>{r.label}</dt>
                <dd>{(r.watts ?? 0).toFixed(1)} W</dd>
              </Fragment>
            ))}
          </dl>
        </section>
      )}
    </div>
  )
}

function SensorTempRow({ reading: r }: { reading: ProxmoxSensorReading }) {
  const level = tempLevel(r.tempC, r.highC, r.critC)
  const limit = r.critC ?? r.highC
  const pct = limit ? Math.min(100, ((r.tempC ?? 0) / limit) * 100) : null
  return (
    <div className="pve-row">
      <div className="pve-row-head">
        <span className="pve-row-name" title={r.chip}>{r.label}</span>
        <HealthBadge level={level} text={`${(r.tempC ?? 0).toFixed(1)} °C`} advice={healthAdvice('temp', level)} />
      </div>
      {pct != null && <Meter pct={pct} level={level} />}
      <div className="pve-row-foot">
        {r.chip}
        {r.highC != null && ` · high ${r.highC.toFixed(0)}°`}
        {r.critC != null && ` · crit ${r.critC.toFixed(0)}°`}
      </div>
    </div>
  )
}

// ── Console tab (V6.8 — SSH shell on the node itself) ─────────────────────────

function ConsoleTab({ connection }: { connection: ProxmoxConnection }) {
  const features = useFeatures()
  const sshConfigured = !!(connection.hasSshPrivateKey && connection.sshHost && connection.sshUsername)
  return (
    <NodeConsolePanel
      connectionId={connection.id}
      sshConfigured={sshConfigured}
      allowConsole={connection.allowConsole}
      allowConsoleGlobal={features.data?.allowProxmoxConsole ?? false}
    />
  )
}

// ── Small shared bits ─────────────────────────────────────────────────────────

function TimeframeBar({ timeframe, onChange, fetching }: {
  timeframe: ProxmoxRrdTimeframe
  onChange: (tf: ProxmoxRrdTimeframe) => void
  fetching: boolean
}) {
  return (
    <div className="container-modal-stats-header">
      <div className="container-modal-actions">
        {TIMEFRAMES.map((t) => (
          <Button key={t.id} type="button" size="sm" variant={timeframe === t.id ? 'default' : 'outline'} onClick={() => onChange(t.id)}>
            {t.label}
          </Button>
        ))}
      </div>
      {fetching && <span className="container-modal-empty"><Loader2 className="h-3.5 w-3.5 inline animate-spin" /> updating…</span>}
    </div>
  )
}

function RefreshedAt({ at }: { at: number }) {
  const now = useNowTick()   // tick live so the label doesn't freeze between polls
  return (
    <div className="container-modal-stats-header">
      <span className="container-modal-empty"><Activity className="h-3.5 w-3.5 inline" /> Refreshed {relativeTime(at, now)}</span>
    </div>
  )
}

function Meter({ pct, level }: { pct: number | null; level: HealthLevel }) {
  return (
    <div className="pve-meter" role="img" aria-label={pct != null ? `${pct.toFixed(0)}%` : 'unknown'}>
      <div className="pve-meter-fill" data-level={level} style={{ width: `${Math.max(0, Math.min(100, pct ?? 0))}%` }} />
    </div>
  )
}

function MeterValue({ pct, level, text, advice }: { pct: number; level: HealthLevel; text: string; advice?: string }) {
  return (
    <div className="pve-meter-value" title={advice}>
      <span>{text}</span>
      <Meter pct={pct} level={level} />
    </div>
  )
}

function HealthBadge({ level, text, advice }: { level: HealthLevel; text: string; advice?: string }) {
  return <span className="pve-health-badge" data-level={level} title={advice}>{text}</span>
}

// ── Formatters (local, mirroring the LXC modal's set) ─────────────────────────

function subscriptionLabel(status: string, level: string | null): string {
  const s = status.toLowerCase()
  if (s === 'active') return level ? `active (${level})` : 'active'
  if (s === 'notfound') return 'no subscription'
  return status
}

function formatBytes(bytes: number | null): string | null {
  if (bytes == null || bytes <= 0) return bytes === 0 ? '0 B' : null
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++ }
  return `${value >= 10 || i === 0 ? Math.round(value) : value.toFixed(1)} ${units[i]}`
}

function formatUptime(seconds: number | null): string | null {
  if (seconds == null || seconds <= 0) return null
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

function formatRate(bytesPerSec: number | undefined): string {
  return `${formatBytes(bytesPerSec ?? 0) ?? '0 B'}/s`
}

function relativeTime(epochMs: number, now: number): string {
  if (!epochMs) return 'never'
  const secs = Math.max(0, Math.round((now - epochMs) / 1000))
  if (secs < 2) return 'just now'
  if (secs < 60) return `${secs}s ago`
  const mins = Math.round(secs / 60)
  if (mins < 60) return `${mins} min ago`
  return `${Math.round(mins / 60)} h ago`
}
