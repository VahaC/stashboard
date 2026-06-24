import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Activity,
  AlertCircle,
  ArchiveRestore,
  Bell,
  BellOff,
  Camera,
  Cpu,
  Download,
  FileText,
  Grid,
  HardDrive,
  Info,
  MemoryStick,
  MoreVertical,
  Network,
  Pencil,
  Play,
  Plus,
  Power,
  RefreshCw,
  Square,
  ScrollText,
  Search,
  Settings,
  SquareChevronRight,
  Thermometer,
  Trash2,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { EntityCard } from '@/components/shared/EntityCard'
import { ContainerIcon } from '@/components/docker/atoms/ContainerIcon'
import { FloatingMenu } from '@/components/shared/FloatingMenu'
import { StateBadge } from '@/components/shared/StateBadge'
import { ProxmoxConnectionModal } from '@/components/ProxmoxConnectionModal'
import { LxcModal, type LxcModalTab } from '@/components/proxmox/LxcModal'
import { LxcCreateModal } from '@/components/proxmox/LxcCreateModal'
import { QemuCreateModal } from '@/components/proxmox/QemuCreateModal'
import { LxcRestoreModal } from '@/components/proxmox/LxcRestoreModal'
import { LxcCloneModal } from '@/components/proxmox/LxcCloneModal'
import { LxcPowerConfirmDialog } from '@/components/proxmox/LxcPowerConfirmDialog'
import { NodeModal, type NodeModalTab } from '@/components/proxmox/NodeModal'
import { ProxmoxUpdateDialog } from '@/components/proxmox/ProxmoxUpdateDialog'
import { ProxmoxBulkUpdateDialog } from '@/components/proxmox/ProxmoxBulkUpdateDialog'
import { useFeatures } from '@/lib/queries'
import {
  useProxmoxConnections,
  useProxmoxGuestIcons,
  useDeleteProxmoxConnection,
  useCheckProxmoxNow,
  useProxmoxLxcAction,
  useProxmoxNodeStatus,
  useSetBulkProxmoxMonitoring,
  useSyncProxmoxLxcStatuses,
} from '@/lib/proxmox-queries'
import {
  THRESHOLDS,
  cpuPercent,
  healthAdvice,
  levelFor,
  memPercent,
  rootPercent,
  showConnectionError,
  worstNodeLevel,
  type HealthMetric,
} from '@/lib/proxmox-node-health'
import type { ProxmoxConnection, ProxmoxGuest, ProxmoxLxcAction } from '@/lib/types'
import {
  computeProxmoxTotals,
  connectionSwitcherStats,
  findDeepLinkGuest,
  guestMatchesFilters,
  isProxmoxNode as isNode,
  isProxmoxQemu as isVm,
  isSnoozeActive,
  type ProxmoxGuestFilters,
  type ProxmoxMonitoringFilter,
  type ProxmoxStateFilter,
  type ProxmoxTypeFilter,
} from '@/lib/proxmox-page'
import { cn, getApiErrorMessage } from '@/lib/utils'
import { useNowTick } from '@/lib/use-now-tick'
import '@/styles/docker-instances.css'
import '@/styles/proxmox.css'

/** Relative time for an epoch-ms timestamp (React Query's `dataUpdatedAt`),
 *  at second granularity since the node health polls every ~20s. `now` is passed
 *  in (from {@link useNowTick}) so the label ticks live instead of freezing. */
const refreshAgo = (epochMs: number, now: number) => {
  if (!epochMs) return 'never'
  const secs = Math.max(0, Math.round((now - epochMs) / 1000))
  if (secs < 2) return 'just now'
  if (secs < 60) return `${secs}s ago`
  const mins = Math.round(secs / 60)
  if (mins < 60) return `${mins} min ago`
  return `${Math.round(mins / 60)} h ago`
}

const formatBytes = (bytes: number | null) => {
  if (bytes == null || bytes <= 0) return null
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value >= 10 || i === 0 ? Math.round(value) : value.toFixed(1)} ${units[i]}`
}

const formatUptime = (seconds: number | null) => {
  if (seconds == null || seconds <= 0) return null
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

/** "SSH not configured" is a config choice, not a failure — distinguish it from
 *  real errors (pct exec failed, not Debian) so the card stays calm. */
const isSshGap = (error: string | null) => !!error && error.startsWith('SSH is not configured')

function GuestCard({ guest, iconDataUri = null, onOpen, onAction, busy = false }: {
  guest: ProxmoxGuest
  /** V7.8 — resolved card avatar (custom upload → official OS icon → null). */
  iconDataUri?: string | null
  onOpen?: (tab?: LxcModalTab) => void
  onAction?: (action: ProxmoxLxcAction) => void
  busy?: boolean
}) {
  const clickable = !!onOpen
  // V6.14 — a QEMU VM reuses the same card surface; only the subtitle (VM vs CT)
  // and the available diagnostic tabs differ (no Logs/Watch/Console for a VM).
  const vm = isVm(guest.guestType)
  // V6.14 — the card's graceful Shutdown goes through a confirm dialog that
  // explains what it does (and how it differs from a hard Stop).
  const [confirmPower, setConfirmPower] = useState<'stop' | 'shutdown' | null>(null)
  const [menuPos, setMenuPos] = useState<{ x: number; y: number } | null>(null)
  const openMenu = (e: { preventDefault(): void; stopPropagation(): void; clientX: number; clientY: number }) => { e.preventDefault(); e.stopPropagation(); setMenuPos({ x: e.clientX, y: e.clientY }) }
  const count = guest.pendingUpdates
  // V6.7 — monitoring off (LXC only) drops the "updates pending" emphasis and
  // mutes the card, mirroring a disabled Docker watch.
  const monitoringOff = !guest.monitoringEnabled
  // V6.11 — an active maintenance snooze mutes the card the same way until it lapses.
  const snoozeActive = isSnoozeActive(guest)
  const muted = monitoringOff || snoozeActive
  const hasUpdates = !muted && count != null && count > 0
  const sshGap = isSshGap(guest.lastError)
  const realError = !muted && !!guest.lastError && !sshGap

  const mem = formatBytes(guest.memoryBytes)
  const disk = formatBytes(guest.diskBytes)
  const uptime = guest.isRunning ? formatUptime(guest.uptimeSeconds) : null

  // ── LXC card — the shared EntityCard, literally the same surface as a Docker
  //    container card. Resources / IP go in the chip row (like Docker ports),
  //    uptime in the status line; the action row mirrors the Docker card's. ──
  const statusText = guest.isRunning ? (uptime ? `Up ${uptime}` : 'Running') : 'Stopped'
  // Resource metrics go in the chip row; the IP address is deliberately kept on
  // its own line below (it's the connectivity address, not a metric).
  const chips: { icon: typeof Cpu; text: string }[] = []
  if (guest.cpuCores != null) chips.push({ icon: Cpu, text: `${guest.cpuCores} vCPU` })
  if (mem) chips.push({ icon: MemoryStick, text: mem })
  if (disk) chips.push({ icon: HardDrive, text: disk })

  const diag = (tab: LxcModalTab, label: string, Icon: typeof Info, disabled = false) => (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      disabled={disabled}
      title={disabled ? `${label} — coming in a later version` : label}
      onClick={() => onOpen?.(tab)}
    >
      <Icon className="h-3.5 w-3.5" />
    </Button>
  )

  return (
    <>
    <EntityCard
      state={guest.isRunning ? 'running' : 'stopped'}
      dimmed={muted}
      name={<span title={guest.name}>{guest.name}</span>}
      icon={<ContainerIcon dataUri={iconDataUri} name={guest.name} />}
      updateBadge={monitoringOff
        ? <span className="docker-section-badge cc-update-badge" data-status="Disabled" title="Update monitoring is off for this container">Disabled</span>
        : snoozeActive
          ? <span className="docker-section-badge cc-update-badge" data-status="Disabled" title={`Snoozed until ${new Date(guest.monitoringSnoozedUntil!).toLocaleString()}`}>Snoozed</span>
          : hasUpdates
            ? <span className="cc-update-badge">{count} update{count === 1 ? '' : 's'}</span>
            : undefined}
      subtitle={`${vm ? 'VM' : 'CT'} ${guest.vmId}`}
      statusLine={statusText}
      chips={chips.length > 0
        ? chips.map((c) => (
          <span key={c.text} className="cc-chip docker-instances-card-port">
            <c.icon className="h-3 w-3" />{c.text}
          </span>
        ))
        : undefined}
      extraMeta={guest.ipAddress
        ? (
          <span className="cc-chip docker-instances-card-port">
            <Network className="h-3 w-3" />{guest.ipAddress}
          </span>
        )
        : undefined}
      clickable={clickable}
      onActivate={() => onOpen?.('overview')}
      onContextMenu={openMenu}
      actionsLeft={
        <>
          {diag('overview', 'Overview', Info)}
          {diag('config', 'Config', Settings)}
          {diag('tasks', 'Tasks', FileText)}
          {diag('snapshots', 'Snapshots', Camera)}
          {/* Logs / Watch / Console are SSH/apt/pct-backed — LXC only. */}
          {!vm && diag('logs', 'Logs', ScrollText)}
          {diag('stats', 'Stats', Activity)}
          {!vm && diag('watch', 'Watch', Bell)}
          {!vm && diag('console', 'Console', SquareChevronRight)}
        </>
      }
      actionsRight={
        guest.isRunning ? (
          <>
            {/* The quick card action is the graceful shutdown — labelled to match
                what it does and the modal's terminology (a hard "Stop" lives in
                the modal's Lifecycle section). */}
            <Button type="button" variant="outline" size="sm" disabled={busy || !onAction}
              title={`Gracefully shut down the ${vm ? 'VM' : 'container'}`} onClick={() => setConfirmPower('shutdown')}>
              <Power className="h-3.5 w-3.5" /><span className="label-text">Shutdown</span>
            </Button>
            <Button type="button" variant="outline" size="sm" disabled={busy || !onAction}
              title={`Reboot the ${vm ? 'VM' : 'container'}`} onClick={() => onAction?.('reboot')}>
              <RefreshCw className={cn('h-3.5 w-3.5', busy && 'animate-spin')} /><span className="label-text">Reboot</span>
            </Button>
          </>
        ) : (
          <Button type="button" variant="outline" size="sm" disabled={busy || !onAction}
            title={`Start the ${vm ? 'VM' : 'container'}`} onClick={() => onAction?.('start')}>
            <Play className="h-3.5 w-3.5" /><span className="label-text">Start</span>
          </Button>
        )
      }
      error={realError ? (guest.lastError ?? undefined) : undefined}
    >
      {sshGap && (
        <p style={{ margin: '0.25rem 0 0', fontSize: '11px', color: 'var(--muted-foreground)' }}>
          Add SSH to this host (Edit) to read its update count.
        </p>
      )}
      {menuPos && (
        <FloatingMenu pos={menuPos} onClose={() => setMenuPos(null)}>
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('overview') }}><Info className="h-3.5 w-3.5" /> Overview</button>
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('config') }}><Settings className="h-3.5 w-3.5" /> Config</button>
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('tasks') }}><FileText className="h-3.5 w-3.5" /> Tasks</button>
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('snapshots') }}><Camera className="h-3.5 w-3.5" /> Snapshots</button>
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('audit') }}><Camera className="h-3.5 w-3.5" /> Audit</button>
          {!vm && <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('logs') }}><ScrollText className="h-3.5 w-3.5" /> Logs</button>}
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('stats') }}><Activity className="h-3.5 w-3.5" /> Stats</button>
          {!vm && <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('watch') }}><Bell className="h-3.5 w-3.5" /> Watch</button>}
          <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen?.('console') }}><SquareChevronRight className="h-3.5 w-3.5" /> Console</button>
          <div className="cgroup-menu-sep" />
          {guest.isRunning ? (
            <>
              <button className="cgroup-menu-item" disabled={busy || !onAction} onClick={() => { setMenuPos(null); setConfirmPower('shutdown') }}><Power className="h-3.5 w-3.5" /> Shutdown</button>
              <button className="cgroup-menu-item" disabled={busy || !onAction} onClick={() => { setMenuPos(null); onAction?.('reboot') }}><RefreshCw className="h-3.5 w-3.5" /> Reboot</button>
              <button className="cgroup-menu-item cgroup-menu-item--danger" disabled={busy || !onAction} onClick={() => { setMenuPos(null); setConfirmPower('stop') }}><Square className="h-3.5 w-3.5" /> Stop</button>
            </>
          ) : (
            <button className="cgroup-menu-item" disabled={busy || !onAction} onClick={() => { setMenuPos(null); onAction?.('start') }}><Play className="h-3.5 w-3.5" /> Start</button>
          )}          
          {/* <div className="cgroup-menu-sep" />
          <button className="cgroup-menu-item" disabled={busy || !onAction} onClick={() => { setMenuPos(null); onAction?.('clone') }}><Copy className="h-3.5 w-3.5" /> Clone</button> */}
        </FloatingMenu>
      )}
    </EntityCard>
    {confirmPower && (
      <LxcPowerConfirmDialog
        open
        action={confirmPower}
        vmId={guest.vmId}
        name={guest.name}
        isVm={vm}
        busy={busy}
        onConfirm={() => { const a = confirmPower; setConfirmPower(null); onAction?.(a) }}
        onCancel={() => setConfirmPower(null)}
      />
    )}
    </>
  )
}

/** V6.8 — one CPU/RAM/storage health signal chip on the node card. Reuses the
 *  shared status colours and (for a degraded value) exposes a tooltip with the
 *  root reason + suggested action. */
function HealthChip({ icon: Icon, label, pct, metric }: {
  icon: typeof Cpu
  label: string
  pct: number
  metric: HealthMetric & ('cpu' | 'mem' | 'root')
}) {
  const level = levelFor(pct, THRESHOLDS[metric])
  const advice = healthAdvice(metric, level)
  return (
    <span
      className="proxmox-node-metric"
      data-level={level}
      title={advice ? `${label} ${pct.toFixed(0)}% — ${advice}` : `${label} ${pct.toFixed(0)}%`}
    >
      <Icon className="h-3 w-3" />{label} {pct.toFixed(0)}%
    </span>
  )
}

/** V6.8 — the PVE node card, styled to follow the Docker page's `.host-card`
 *  pattern (full-width host summary above the LXC grid) for cross-platform
 *  consistency. Polls the live node status for a high-level CPU/RAM/storage
 *  health summary, reuses the shared status dot + {@link StateBadge}, and opens
 *  the detailed {@link NodeModal} on click. The update badge / "Update now"
 *  affordance from the scan row is preserved. */
function NodeCard({ guest, connection, onOpen, onApplyUpdate, canApplyUpdates = false, onCheckNow, checking = false }: {
  guest: ProxmoxGuest
  connection: ProxmoxConnection
  onOpen: (tab?: NodeModalTab) => void
  onApplyUpdate?: () => void
  canApplyUpdates?: boolean
  /** V6.8 — re-scan the node (the connection == one node), moved here from the
   *  connection header so the refresh action lives with the node it refreshes. */
  onCheckNow?: () => void
  checking?: boolean
}) {
  const status = useProxmoxNodeStatus(connection.id)
  const now = useNowTick()   // so "Refreshed Xs ago" ticks live between polls
  const [menuPos, setMenuPos] = useState<{ x: number; y: number } | null>(null)
  const count = guest.pendingUpdates
  const hasUpdates = count != null && count > 0
  const sshGap = isSshGap(guest.lastError)
  const realError = !!guest.lastError && !sshGap

  const s = status.data
  const online = !!s
  const cpu = s ? cpuPercent(s) : null
  const mem = s ? memPercent(s) : null
  const root = s ? rootPercent(s) : null
  const worst = s ? worstNodeLevel(s) : 'ok'
  const uptime = s ? formatUptime(s.uptimeSeconds) : null

  const updateBadge = realError
    ? { label: 'Error', cls: 'proxmox-badge-error' }
    : count == null
      ? { label: sshGap ? 'Updates n/a' : 'Unknown', cls: 'proxmox-badge-muted' }
      : hasUpdates
        ? { label: `${count} update${count === 1 ? '' : 's'}`, cls: 'proxmox-badge-update' }
        : { label: 'Up to date', cls: 'proxmox-badge-ok' }

  return (
    <div
      className={cn('host-card proxmox-node-host-card', hasUpdates && 'proxmox-guest-card-update')}
      role="button"
      tabIndex={0}
      onClick={() => onOpen('overview')}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen('overview') } }}
      onContextMenu={(e) => { e.preventDefault(); e.stopPropagation(); setMenuPos({ x: e.clientX, y: e.clientY }) }}
    >
      <div className="host-card-top">
        <span className="host-name">
          <span className="host-dot" data-level={worst} data-off={!online} />
          {guest.name}
          {/* Update badge sits between the name and the online/offline pill. */}
          <span className={cn('proxmox-badge', updateBadge.cls)}>{updateBadge.label}</span>
          <StateBadge state={online ? 'online' : 'offline'} size="sm" />
        </span>
      </div>

      {/* Endpoint + uptime on their own line, below the name/actions row. */}
      <span className="host-endpoint proxmox-node-endpoint">
        <span className="tp">{connection.serverType === 'Pbs' ? 'PBS node' : 'node'}</span>
        <span>{connection.apiBaseUrl}</span>
        {uptime && <span className="host-count">· up {uptime}</span>}
      </span>

      <div className="proxmox-node-signals">
        {s ? (
          <>
            {cpu != null && <HealthChip icon={Cpu} label="CPU" pct={cpu} metric="cpu" />}
            {mem != null && <HealthChip icon={MemoryStick} label="RAM" pct={mem} metric="mem" />}
            {root != null && <HealthChip icon={HardDrive} label="Root" pct={root} metric="root" />}
            <span className="proxmox-node-refresh" title="Time since the node health was last polled">
              Refreshed {refreshAgo(status.dataUpdatedAt, now)}
            </span>
          </>
        ) : status.isLoading ? (
          <span className="proxmox-guest-hint">Loading node health…</span>
        ) : (
          <span className="proxmox-guest-hint">Node health unavailable — open for details.</span>
        )}
        {realError && <span className="proxmox-guest-error" title={guest.lastError!}>{guest.lastError}</span>}
        {sshGap && <span className="proxmox-guest-hint">· add SSH to read its update count</span>}
      </div>

      {/* Action row: quick-open buttons for each node-modal tab on the left (the
          node analogue of the LXC card's diagnostics row), Check now / Update now
          on the right. stopPropagation so a button opens its tab / runs its action
          rather than the card's default Overview. On mobile the right group wraps
          below. */}
      <div className="proxmox-node-tabs" onClick={(e) => e.stopPropagation()}>
        <div className="proxmox-node-tabs-left">
          {([
            ['overview', 'Overview', Info],
            ['cpuram', 'CPU / RAM', Cpu],
            ['storage', 'Storage / SMART', HardDrive],
            ['network', 'Network', Network],
            ['sensors', 'Sensors', Thermometer],
            ['alerts', 'Alerts', Bell],
            ['console', 'Console', SquareChevronRight],
          ] as ReadonlyArray<[NodeModalTab, string, typeof Info]>).map(([tab, label, Icon]) => (
            <Button key={tab} type="button" variant="ghost" size="sm" title={label} onClick={() => onOpen(tab)}>
              <Icon className="h-3.5 w-3.5" />
            </Button>
          ))}
        </div>
        <div className="proxmox-node-tabs-right">
          {onCheckNow && (
            <Button type="button" variant="outline" size="sm" onClick={() => onCheckNow()} disabled={checking}>
              <RefreshCw className={cn('h-3.5 w-3.5', checking && 'animate-spin')} /> Check now
            </Button>
          )}
          {canApplyUpdates && (
            <Button type="button" variant="outline" size="sm" onClick={() => onApplyUpdate?.()}>
              <RefreshCw className="h-3.5 w-3.5" /> Update now
            </Button>
          )}
        </div>
      </div>
      {menuPos && (
        <FloatingMenu pos={menuPos} onClose={() => setMenuPos(null)}>
          {([
            ['overview',  'Overview',        Info],
            ['cpuram',    'CPU / RAM',       Cpu],
            ['storage',   'Storage / SMART', HardDrive],
            ['network',   'Network',         Network],
            ['sensors',   'Sensors',         Thermometer],
            ['alerts',    'Alerts',          Bell],
            ['console',   'Console',         SquareChevronRight],
          ] as ReadonlyArray<[NodeModalTab, string, typeof Info]>).map(([tab, label, Icon]) => (
            <button key={tab} className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen(tab) }}>
              <Icon className="h-3.5 w-3.5" /> {label}
            </button>
          ))}
          {(onCheckNow || canApplyUpdates) && <div className="cgroup-menu-sep" />}
          {onCheckNow && (
            <button className="cgroup-menu-item" disabled={checking} onClick={() => { setMenuPos(null); onCheckNow() }}>
              <RefreshCw className={cn('h-3.5 w-3.5', checking && 'animate-spin')} /> Check now
            </button>
          )}
          {canApplyUpdates && (
            <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onApplyUpdate?.() }}>
              <Download className="h-3.5 w-3.5" /> Update now
            </button>
          )}
        </FloatingMenu>
      )}
    </div>
  )
}

function ConnectionBlock({
  connection,
  filters,
  deepLinkVmId,
  onDeepLinkHandled,
  onEdit,
}: {
  connection: ProxmoxConnection
  filters: ProxmoxGuestFilters
  /** V6.10 — vmid the page asked us to open via `?connection=&vmid=`, or null. */
  deepLinkVmId: number | null
  onDeepLinkHandled: () => void
  onEdit: (c: ProxmoxConnection) => void
}) {
  const checkNow = useCheckProxmoxNow()
  const del = useDeleteProxmoxConnection()
  const lxcAction = useProxmoxLxcAction(connection.id)
  // V6.14 — VM cards route their lifecycle actions to the qemu/* endpoints.
  const qemuAction = useProxmoxLxcAction(connection.id, 'qemu')
  const bulkMonitoring = useSetBulkProxmoxMonitoring(connection.id)
  // V6.13.1 — keep the LXC cards' running state live (a guest stopped/started
  // outside Stashboard is reflected within ~20s, not at the next scheduled scan).
  useSyncProxmoxLxcStatuses(connection.id, connection.enabled)
  const features = useFeatures()
  // V7.2.1 — the live node-status poll doubles as the host's current-reachability
  // signal. A connection-level scan error (e.g. a brief "API unreachable: No
  // route to host" from a scan that ran while the host was momentarily down)
  // lingers on `connection.lastError` until the next successful scan — but the
  // node card polls independently and may already show the host green/online.
  // When the live status currently succeeds, that banner is stale and
  // contradicts the card, so we suppress it rather than show both at once.
  const nodeReachable = !!useProxmoxNodeStatus(connection.id).data
  const [connMenuPos, setConnMenuPos] = useState<{ x: number; y: number } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [open, setOpen] = useState<{ guest: ProxmoxGuest; tab: LxcModalTab } | null>(null)
  const [nodeOpen, setNodeOpen] = useState<NodeModalTab | null>(null)
  const [nodeUpdateOpen, setNodeUpdateOpen] = useState(false)
  // V6.11 — host-wide bulk controls.
  const [confirmBulk, setConfirmBulk] = useState<'enable' | 'disable' | null>(null)
  const [bulkUpdateOpen, setBulkUpdateOpen] = useState(false)
  // V6.13.1 — New LXC. V8.4 — New VM (QEMU).
  const [createOpen, setCreateOpen] = useState(false)
  const [createVmOpen, setCreateVmOpen] = useState(false)
  // V8.1 — Restore LXC from a backup archive. V8.3 — Restore VM (separate modal kind).
  const [restoreOpen, setRestoreOpen] = useState(false)
  const [restoreVmOpen, setRestoreVmOpen] = useState(false)
  const [cloneGuest, setCloneGuest] = useState<ProxmoxGuest | null>(null)

  const totalPending = connection.guests.reduce((sum, g) => sum + (g.pendingUpdates ?? 0), 0)

  // The node card is the host summary (always shown); the LXC + VM cards (V6.14)
  // run through the page's search / state / monitoring / type filters.
  const lxcGuests = connection.guests.filter((g) => !isNode(g.guestType))
  const filteredLxc = lxcGuests.filter((g) => guestMatchesFilters(g, filters))
  // V7.8 — resolved card avatars (custom upload → official OS icon) keyed by vmId.
  const guestIcons = useProxmoxGuestIcons(connection.id)

  // V6.10 — open the LXC modal when the page resolves a `?…&vmid=` deep link
  // to one of this connection's guests.
  useEffect(() => {
    if (deepLinkVmId == null) return
    const target = lxcGuests.find((g) => g.vmId === deepLinkVmId)
    if (target) setOpen({ guest: target, tab: 'overview' })
    onDeepLinkHandled()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deepLinkVmId])

  const sshConfigured = !!(connection.hasSshPrivateKey && connection.sshHost && connection.sshUsername)
  const canApplyUpdates = (features.data?.allowProxmoxUpdates ?? false) && connection.allowUpdates && sshConfigured
  // V6.13.1 — the New LXC affordance needs the global switch + the per-host
  // opt-in (the server re-checks both regardless). Not for PBS (no guests).
  const canCreate = (features.data?.allowProxmoxCreate ?? false) && connection.allowCreate
    && connection.serverType !== 'Pbs'
  // V8.1 — the Restore LXC affordance needs the global switch + the per-host opt-in
  // (the server re-checks both regardless). Not for PBS (no node-storage backups here).
  const canRestore = (features.data?.allowProxmoxRestore ?? false) && connection.allowRestore
    && connection.serverType !== 'Pbs'
  const hasLxc = lxcGuests.length > 0

  return (
    <section className="proxmox-conn" onContextMenu={(e) => {
      // A right-click inside an open modal (LxcModal, NodeModal, …) bubbles up
      // the React tree to here even though the modal is portaled to <body> — so
      // ignore events that originated inside a dialog, otherwise the host menu
      // hijacks the modal's right-click.
      if ((e.target as HTMLElement).closest('[role="dialog"]')) return
      e.preventDefault(); setConnMenuPos({ x: e.clientX, y: e.clientY })
    }}>
      <div className="proxmox-conn-header">
        <div className="proxmox-conn-title">
          <h2>{connection.name}</h2>
          <span className="proxmox-conn-meta">
            {/* The node name lives on the node card below — here we keep only the
                connection-wide roll-up (count + total pending across node + LXCs). */}
            {connection.guests.length} object{connection.guests.length === 1 ? '' : 's'}
            {totalPending > 0 && <span className="proxmox-conn-total"> · {totalPending} update{totalPending === 1 ? '' : 's'} pending</span>}
          </span>
        </div>
        <div className="proxmox-conn-actions">
          <Button
            variant="outline"
            size="sm"
            onMouseDown={(e) => e.stopPropagation()}
            onClick={(e) => { const r = e.currentTarget.getBoundingClientRect(); setConnMenuPos(p => p ? null : { x: r.left, y: r.bottom + 4 }) }}
            title="Host actions"
            className="cgroup-menu-trigger"
          >
            <MoreVertical className="h-3.5 w-3.5" />
          </Button>
          {connMenuPos && (
            <FloatingMenu pos={connMenuPos} onClose={() => setConnMenuPos(null)}>
              {canCreate && (
                <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); setCreateOpen(true) }}>
                  <Plus className="h-3.5 w-3.5" /> New LXC
                </button>
              )}
              {canCreate && (
                <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); setCreateVmOpen(true) }}>
                  <Plus className="h-3.5 w-3.5" /> New VM
                </button>
              )}
              {canRestore && (
                <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); setRestoreOpen(true) }}>
                  <ArchiveRestore className="h-3.5 w-3.5" /> Restore LXC
                </button>
              )}
              {canRestore && (
                <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); setRestoreVmOpen(true) }}>
                  <ArchiveRestore className="h-3.5 w-3.5" /> Restore VM
                </button>
              )}
              {(canCreate || canRestore) && <div className="cgroup-menu-sep" />}
              {hasLxc && (
                <>
                  <button
                    className="cgroup-menu-item"
                    disabled={bulkMonitoring.isPending}
                    onClick={() => { setConnMenuPos(null); setConfirmBulk('enable') }}
                  >
                    <Bell className="h-3.5 w-3.5" /> Enable all
                  </button>
                  <button
                    className="cgroup-menu-item"
                    disabled={bulkMonitoring.isPending}
                    onClick={() => { setConnMenuPos(null); setConfirmBulk('disable') }}
                  >
                    <BellOff className="h-3.5 w-3.5" /> Disable all
                  </button>
                  {canApplyUpdates && (
                    <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); setBulkUpdateOpen(true) }}>
                      <RefreshCw className="h-3.5 w-3.5" /> Update all
                    </button>
                  )}
                  <div className="cgroup-menu-sep" />
                </>
              )}
              <button className="cgroup-menu-item" onClick={() => { setConnMenuPos(null); onEdit(connection) }}>
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
              <button
                className="cgroup-menu-item cgroup-menu-item--danger"
                onClick={() => { setConnMenuPos(null); setDeleteError(null); setConfirmDelete(true) }}
              >
                <Trash2 className="h-3.5 w-3.5" /> Delete
              </button>
            </FloatingMenu>
          )}
        </div>
      </div>

      {showConnectionError(connection.lastError, nodeReachable) && <p className="proxmox-conn-error">{connection.lastError}</p>}
      {!connection.enabled && <p className="proxmox-conn-disabled">Scanning disabled — enable it from Edit.</p>}

      {connection.guests.length === 0 ? (
        <div className="proxmox-conn-empty">
          <p>No guests discovered yet. The next scheduled scan (or “Check now”) will populate this.</p>
          <Button variant="outline" size="sm" className="mt-2" onClick={() => checkNow.mutate(connection.id)} disabled={checkNow.isPending}>
            <RefreshCw className={cn('h-3.5 w-3.5', checkNow.isPending && 'animate-spin')} /> Check now
          </Button>
        </div>
      ) : (
        <>
          {/* The node renders as a full-width `.host-card` summary (Docker-page
              parity), with the LXC cards packed in the grid below it. */}
          {connection.guests.filter((g) => isNode(g.guestType)).map((g) => (
            <NodeCard
              key={`${g.guestType}-${g.vmId}`}
              guest={g}
              connection={connection}
              onOpen={(tab) => setNodeOpen(tab ?? 'overview')}
              canApplyUpdates={canApplyUpdates}
              onApplyUpdate={() => setNodeUpdateOpen(true)}
              onCheckNow={() => checkNow.mutate(connection.id)}
              checking={checkNow.isPending}
            />
          ))}
          {lxcGuests.length > 0 && (
            filteredLxc.length === 0 ? (
              <p className="proxmox-guest-nomatch">No guests match the current filters.</p>
            ) : (
              <div className="proxmox-guest-grid">
                {filteredLxc.map((g) => {
                  const act = isVm(g.guestType) ? qemuAction : lxcAction
                  return (
                    <GuestCard
                      key={`${g.guestType}-${g.vmId}`}
                      guest={g}
                      iconDataUri={guestIcons.data?.[String(g.vmId)] ?? null}
                      onOpen={(tab) => setOpen({ guest: g, tab: tab ?? 'overview' })}
                      onAction={(action) => {
                        if (action === 'clone') { setCloneGuest(g); return }
                        act.mutate({ vmId: g.vmId, action })
                      }}
                      busy={act.isPending && act.variables?.vmId === g.vmId}
                    />
                  )
                })}
              </div>
            )
          )}
        </>
      )}

      {open && (
        <LxcModal
          // Re-derive the guest from the live connection data each render so
          // optimistic mutations (monitoring toggle, snooze) reflect in the open
          // modal immediately — `open.guest` is only the snapshot at open time.
          guest={connection.guests.find((g) => g.vmId === open.guest.vmId) ?? open.guest}
          connection={connection}
          initialTab={open.tab}
          onClose={() => setOpen(null)}
        />
      )}

      {nodeOpen && (
        <NodeModal
          connection={connection}
          initialTab={nodeOpen}
          onClose={() => setNodeOpen(null)}
        />
      )}

      {nodeUpdateOpen && (
        <ProxmoxUpdateDialog
          connectionId={connection.id}
          vmId={0}
          targetLabel={`${connection.nodeName} (node)`}
          onClose={() => setNodeUpdateOpen(false)}
        />
      )}

      {bulkUpdateOpen && (
        <ProxmoxBulkUpdateDialog
          connectionId={connection.id}
          guests={connection.guests}
          onClose={() => setBulkUpdateOpen(false)}
        />
      )}

      {createOpen && (
        <LxcCreateModal connection={connection} onClose={() => setCreateOpen(false)} />
      )}
      {createVmOpen && (
        <QemuCreateModal connection={connection} onClose={() => setCreateVmOpen(false)} />
      )}

      {restoreOpen && (
        <LxcRestoreModal connection={connection} onClose={() => setRestoreOpen(false)} />
      )}
      {restoreVmOpen && (
        <LxcRestoreModal connection={connection} isVm onClose={() => setRestoreVmOpen(false)} />
      )}

      {cloneGuest && (
        <LxcCloneModal connection={connection} guest={cloneGuest} onClose={() => setCloneGuest(null)} />
      )}

      {/* V6.11 — confirm before flipping monitoring for every LXC on the host. */}
      <Dialog open={confirmBulk !== null} onOpenChange={(v) => { if (!v && !bulkMonitoring.isPending) setConfirmBulk(null) }}>
        <DialogContent className="remove-confirm-dialog">
          <DialogHeader>
            <DialogTitle>
              {confirmBulk === 'disable' ? 'Disable monitoring for all containers?' : 'Enable monitoring for all containers?'}
            </DialogTitle>
            <DialogDescription className="sr-only">
              Confirm bulk monitoring change for {connection.name}
            </DialogDescription>
          </DialogHeader>
          <p className="remove-confirm-warning">
            {confirmBulk === 'disable'
              ? <>This turns update monitoring <strong>off</strong> for every LXC on <strong>{connection.name}</strong>. Disabled containers are skipped by scheduled and manual checks and excluded from notifications. The node row is unaffected.</>
              : <>This turns update monitoring <strong>on</strong> for every LXC on <strong>{connection.name}</strong>, including them in the host's scheduled checks and notifications again.</>}
          </p>
          <DialogFooter>
            <Button type="button" variant="outline" disabled={bulkMonitoring.isPending} onClick={() => setConfirmBulk(null)}>
              Cancel
            </Button>
            <Button
              type="button"
              variant={confirmBulk === 'disable' ? 'destructive' : 'default'}
              disabled={bulkMonitoring.isPending}
              onClick={() => {
                bulkMonitoring.mutate(confirmBulk === 'enable', { onSettled: () => setConfirmBulk(null) })
              }}
            >
              {confirmBulk === 'disable' ? <BellOff className="h-3.5 w-3.5" /> : <Bell className="h-3.5 w-3.5" />}
              {bulkMonitoring.isPending ? 'Applying…' : (confirmBulk === 'disable' ? 'Disable all' : 'Enable all')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={confirmDelete} onOpenChange={(v) => { if (!v && !del.isPending) setConfirmDelete(false) }}>
        <DialogContent className="remove-confirm-dialog">
          <DialogHeader>
            <DialogTitle>Delete Proxmox host?</DialogTitle>
            <DialogDescription className="sr-only">
              Confirm deletion of Proxmox host {connection.name}
            </DialogDescription>
          </DialogHeader>

          <dl className="remove-confirm-summary">
            <dt>Host</dt>
            <dd>{connection.name}</dd>
            <dt>Node</dt>
            <dd>{connection.nodeName}</dd>
            <dt>Objects</dt>
            <dd>{connection.guests.length} node/LXC card{connection.guests.length === 1 ? '' : 's'}</dd>
          </dl>

          <p className="remove-confirm-warning">
            This permanently removes the Proxmox host from Stashboard — its API token / SSH
            credentials, the discovered node and LXC cards, and its update schedule. It does
            <strong> not</strong> touch the Proxmox host itself or any of its containers.
          </p>

          {deleteError && (
            <p className="remove-confirm-error">
              <AlertCircle className="h-3.5 w-3.5 inline" /> {deleteError}
            </p>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" disabled={del.isPending} onClick={() => setConfirmDelete(false)}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={del.isPending}
              onClick={() => {
                setDeleteError(null)
                del.mutate(connection.id, {
                  onError: (e) => setDeleteError(getApiErrorMessage(e) ?? 'Failed to delete the host'),
                  onSuccess: () => setConfirmDelete(false),
                })
              }}
            >
              <Trash2 className="h-3.5 w-3.5" />
              {del.isPending ? 'Deleting…' : 'Delete host'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  )
}

export function Proxmox() {
  const { data: connections = [], isLoading } = useProxmoxConnections()
  const [searchParams, setSearchParams] = useSearchParams()
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<ProxmoxConnection | null>(null)
  const [modalKey, setModalKey] = useState(0)

  // V6.10 — page-level Docker-parity controls (search / state / monitoring /
  // host), lifted here so the summary strip + switcher + every section agree.
  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState<ProxmoxStateFilter>('all')
  const [monitoringFilter, setMonitoringFilter] = useState<ProxmoxMonitoringFilter>('all')
  const [typeFilter, setTypeFilter] = useState<ProxmoxTypeFilter>('all')
  const [activeHost, setActiveHost] = useState<string>('all')
  const [deepLink, setDeepLink] = useState<{ connectionId: string; vmId: number } | null>(null)
  const [handledDeepLink, setHandledDeepLink] = useState<string | null>(null)

  const openNew = () => { setEditing(null); setModalKey((k) => k + 1); setModalOpen(true) }
  const openEdit = (c: ProxmoxConnection) => { setEditing(c); setModalKey((k) => k + 1); setModalOpen(true) }

  const filters = useMemo<ProxmoxGuestFilters>(
    () => ({ search, state: stateFilter, monitoring: monitoringFilter, type: typeFilter }),
    [search, stateFilter, monitoringFilter, typeFilter],
  )

  const totals = useMemo(() => computeProxmoxTotals(connections), [connections])

  // V6.14 — only surface the LXC/VM type filter once the user actually has VMs,
  // so a pure-LXC homelab doesn't see a meaningless toggle.
  const hasVms = useMemo(
    () => connections.some((c) => c.guests.some((g) => isVm(g.guestType))),
    [connections],
  )

  // ── Deep-link: open the LXC modal when `?connection=…&vmid=…` ───────────
  const deepLinkConnectionId = searchParams.get('connection')
  const deepLinkVmid = searchParams.get('vmid')
  const deepLinkKey = deepLinkConnectionId && deepLinkVmid
    ? `${deepLinkConnectionId}::${deepLinkVmid}`
    : null

  useEffect(() => {
    if (!deepLinkKey || deepLinkKey === handledDeepLink) return
    if (isLoading) return
    const target = findDeepLinkGuest(connections, deepLinkConnectionId, deepLinkVmid)
    setHandledDeepLink(deepLinkKey)
    if (!target) return
    setActiveHost(target.connection.id)
    setDeepLink({ connectionId: target.connection.id, vmId: target.guest.vmId })
    const next = new URLSearchParams(searchParams)
    next.delete('vmid')
    setSearchParams(next, { replace: true })
  }, [
    deepLinkKey, handledDeepLink, isLoading, connections,
    deepLinkConnectionId, deepLinkVmid, searchParams, setSearchParams,
  ])

  const visibleConnections = activeHost === 'all'
    ? connections
    : connections.filter((c) => c.id === activeHost)

  return (
    <div className="dock">
      <header className="dock-header">
        <div className="dock-header-text">
          <h1 className="dock-title">Proxmox</h1>
        </div>
        <Button className="dock-header-add" onClick={openNew}>
          <Plus className="h-3.5 w-3.5" /> Add Proxmox host
        </Button>
      </header>

      {isLoading ? (
        <p className="dock-empty">Loading…</p>
      ) : connections.length === 0 ? (
        <p className="dock-empty">
          No Proxmox hosts yet. Click <strong>Add Proxmox host</strong> to monitor pending LXC and node package updates.
        </p>
      ) : (
        <>
          <div className="dock-summary">
            <div className="sumtile">
              <div className="sumtile-k"><Grid className="h-3 w-3" />Objects</div>
              <div className="sumtile-v">{totals.objects}<small>/ {totals.hosts} host{totals.hosts === 1 ? '' : 's'}</small></div>
            </div>
            <div className="sumtile is-accent">
              <div className="sumtile-k">Running</div>
              <div className="sumtile-v">{totals.running}</div>
            </div>
            <div className="sumtile is-down">
              <div className="sumtile-k">Stopped</div>
              <div className="sumtile-v">{totals.stopped}</div>
            </div>
            <div className="sumtile is-warn">
              <div className="sumtile-k"><Download className="h-3 w-3" />Updates</div>
              <div className="sumtile-v">{totals.updates}</div>
            </div>
          </div>

          <ConnectionSwitcher
            connections={connections}
            active={activeHost}
            onChange={setActiveHost}
          />

          <div className="dock-toolbar">
            <div className="searchbox">
              <Search className="ic" aria-hidden />
              <input
                className="input"
                placeholder="Search by name…"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                aria-label="Search containers"
              />
            </div>
            <div className="segmented" role="group" aria-label="State filter">
              {(['all', 'running', 'stopped'] as const).map((v) => (
                <button key={v} type="button" aria-pressed={stateFilter === v} onClick={() => setStateFilter(v)}>
                  {v[0].toUpperCase() + v.slice(1)}
                </button>
              ))}
            </div>
            <div className="segmented" role="group" aria-label="Monitoring filter">
              {([
                ['all', 'All'],
                ['enabled', 'Enabled'],
                ['disabled', 'Disabled'],
                ['updates', 'Updates'],
              ] as const).map(([v, label]) => (
                <button key={v} type="button" aria-pressed={monitoringFilter === v} onClick={() => setMonitoringFilter(v)}>
                  {label}
                </button>
              ))}
            </div>
            {/* V6.14 — LXC vs VM, shown only when the user has at least one VM. */}
            {hasVms && (
              <div className="segmented" role="group" aria-label="Type filter">
                {([
                  ['all', 'All'],
                  ['lxc', 'LXC'],
                  ['vm', 'VM'],
                ] as const).map(([v, label]) => (
                  <button key={v} type="button" aria-pressed={typeFilter === v} onClick={() => setTypeFilter(v)}>
                    {label}
                  </button>
                ))}
              </div>
            )}
          </div>

          <div className="host-section proxmox-conn-list">
            {visibleConnections.map((c) => (
              <ConnectionBlock
                key={c.id}
                connection={c}
                filters={filters}
                deepLinkVmId={deepLink?.connectionId === c.id ? deepLink.vmId : null}
                onDeepLinkHandled={() => setDeepLink(null)}
                onEdit={openEdit}
              />
            ))}
          </div>
        </>
      )}

      <ProxmoxConnectionModal key={modalKey} open={modalOpen} onOpenChange={setModalOpen} connection={editing} />
    </div>
  )
}

// ── Connection switcher — the Proxmox analogue of the Docker page's, reusing
//    the same `.switcher` / `.conn` markup + CSS. Hidden for a single host. ──
function ConnectionSwitcher({
  connections,
  active,
  onChange,
}: {
  connections: ProxmoxConnection[]
  active: string
  onChange: (next: string) => void
}) {
  if (connections.length <= 1) return null
  return (
    <div className="switcher" role="tablist" aria-label="Connection">
      <button type="button" className="conn" aria-pressed={active === 'all'} onClick={() => onChange('all')}>
        <span className="conn-name">All connections</span>
        <span className="conn-count">{connections.length} host{connections.length === 1 ? '' : 's'}</span>
      </button>
      {connections.map((conn) => {
        const stats = connectionSwitcherStats(conn)
        return (
          <button
            key={conn.id}
            type="button"
            className="conn"
            aria-pressed={active === conn.id}
            onClick={() => onChange(conn.id)}
            title={conn.name}
          >
            <span className="conn-dot" data-off={!stats.online} />
            <span className="conn-name">{conn.name}</span>
            <span className="conn-count">{stats.running}/{stats.total}</span>
            {stats.updates > 0 && <span className="conn-updates">{stats.updates}</span>}
          </button>
        )
      })}
    </div>
  )
}
