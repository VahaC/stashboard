import { Fragment, useEffect, useMemo, useState } from 'react'
import {
  Activity,
  AlertCircle,
  Bell,
  Boxes,
  Check,
  CheckCircle2,
  Copy,
  ExternalLink,
  FileText,
  HardDrive,
  Info,
  Loader2,
  Network,
  Pencil,
  Play,
  Plus,
  Power,
  RefreshCw,
  RotateCcw,
  Save,
  ScrollText,
  Settings,
  Square,
  SquareChevronRight,
  Trash2,
  TriangleAlert,
  XCircle,
} from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { ContainerStateBadge } from '@/components/docker/atoms/ContainerStateBadge'
import { ContainerIcon } from '@/components/docker/atoms/ContainerIcon'
import { StatTile } from '@/components/shared/StatTile'
import {
  fetchProxmoxLxcStatus,
  useCheckProxmoxLxc,
  useDestroyProxmoxLxc,
  useProxmoxLxcAction,
  useProxmoxGuestIcons,
  useProxmoxLxcConfig,
  useProxmoxLxcRrd,
  useProxmoxLxcTasks,
  useProxmoxTaskLog,
  useProxmoxUpdateCommand,
  useResetGuestIcon,
  useSetProxmoxGuestService,
  useSetProxmoxLxcMonitoring,
  useSnoozeProxmoxLxc,
  useUpdateProxmoxLxcConfig,
  useUploadGuestIcon,
  type ProxmoxGuestKind,
  type ProxmoxRrdTimeframe,
} from '@/lib/proxmox-queries'
import { isProxmoxQemu } from '@/lib/proxmox-page'
import type {
  ProxmoxConnection,
  ProxmoxGuest,
  ProxmoxLxcAction,
  ProxmoxLxcConfigUpdate,
  ProxmoxLxcDetail,
  ProxmoxLxcMountChange,
  ProxmoxLxcNetChange,
  ProxmoxLxcRootfsChange,
  ProxmoxTask,
} from '@/lib/types'
import {
  formatMount,
  formatNet,
  formatRootfs,
  hasUnknownOptions,
  parseMount,
  parseNet,
  parseRootfs,
} from '@/lib/proxmox-lxc-config'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Label } from '@/components/ui/label'
import { useFeatures, useServices } from '@/lib/queries'
import { Link } from 'react-router-dom'
import { LxcConsolePanel } from '@/components/proxmox/LxcConsolePanel'
import { LxcDestroyDialog } from '@/components/proxmox/LxcDestroyDialog'
import { LxcPowerConfirmDialog, type LxcPowerAction } from '@/components/proxmox/LxcPowerConfirmDialog'
import { LxcLogsPanel } from '@/components/proxmox/LxcLogsPanel'
import { ProxmoxUpdateDialog } from '@/components/proxmox/ProxmoxUpdateDialog'
import { cn, getApiErrorMessage } from '@/lib/utils'
import { useNowTick } from '@/lib/use-now-tick'
// Reuse the Docker container modal's styles verbatim so the LXC modal is the
// same surface, not a parallel one. These global stylesheets are imported here
// (the component) rather than relying on a page chunk, because /proxmox and
// /docker are separate lazy routes.
import '@/styles/docker-instances.css'  // .container-modal-* shell
import '@/styles/service-modal.css'     // .docker-stats-* tiles + sparkline

/**
 * Modal opened when the user clicks an LXC card on the Proxmox page. It reuses
 * the **exact** Docker container modal shell (`container-modal-*` classes, the
 * shared {@link ContainerStateBadge}, the `docker-stats-*` stat tiles) so the
 * Docker and Proxmox experiences are one and the same.
 *
 * Tabs mirror the Docker modal: Overview · Config (≈Inspect) · Tasks (PVE task
 * history) · Logs (≈the Docker live log tail, V6.12) · Stats · Watch · Console
 * (≈Exec, V6.6).
 */
export type LxcModalTab = 'overview' | 'config' | 'stats' | 'tasks' | 'logs' | 'watch' | 'console'

interface LxcModalProps {
  guest: ProxmoxGuest
  connection: ProxmoxConnection
  /** Tab to open on mount. Falls back to Overview if that tab isn't ready yet. */
  initialTab?: LxcModalTab
  onClose: () => void
}

const TABS: ReadonlyArray<{ id: LxcModalTab; label: string; icon: typeof Info; ready: boolean }> = [
  { id: 'overview', label: 'Overview', icon: Info, ready: true },
  { id: 'config', label: 'Config', icon: Settings, ready: true },
  { id: 'tasks', label: 'Tasks', icon: FileText, ready: true },
  { id: 'logs', label: 'Logs', icon: ScrollText, ready: true },
  { id: 'stats', label: 'Stats', icon: Activity, ready: true },
  { id: 'watch', label: 'Watch', icon: Bell, ready: true },
  { id: 'console', label: 'Console', icon: SquareChevronRight, ready: true },
]

export function LxcModal({ guest, connection, initialTab = 'overview', onClose }: LxcModalProps) {
  // V6.14 — a QEMU VM reuses the same modal shell but exposes only the tabs that
  // generalise to a VM: Overview · Config (read-only) · Tasks · Stats. The
  // SSH/apt/pct-backed tabs (Watch update-monitoring, Logs, Console) are LXC-only.
  const isVm = isProxmoxQemu(guest.guestType)
  const kind: ProxmoxGuestKind = isVm ? 'qemu' : 'lxc'
  const tabs = useMemo(
    () => (isVm ? TABS.filter((t) => t.id === 'overview' || t.id === 'config' || t.id === 'tasks' || t.id === 'stats') : TABS),
    [isVm],
  )

  const [tab, setTab] = useState<LxcModalTab>(
    () => (tabs.find((t) => t.id === initialTab)?.ready ? initialTab : 'overview'),
  )

  // V6.12 — the Console and Logs tabs hold a live SSH session. Keep them mounted
  // once visited (hidden when not the active tab) so the session survives tab
  // switches; they only unmount — and tear down SSH — when the modal closes. The
  // remaining tabs stay mount-on-demand so they refetch on each visit.
  const [seenConsole, setSeenConsole] = useState(tab === 'console')
  const [seenLogs, setSeenLogs] = useState(tab === 'logs')
  useEffect(() => {
    if (tab === 'console') setSeenConsole(true)
    else if (tab === 'logs') setSeenLogs(true)
  }, [tab])

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <Boxes className="h-4 w-4" />
            <span>{guest.name}</span>
            <ContainerStateBadge state={guest.isRunning ? 'running' : 'stopped'} />
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            {isVm ? 'VM' : 'CT'} {guest.vmId} · {connection.nodeName}
          </DialogDescription>
        </DialogHeader>

        <nav className="container-modal-tabs" role="tablist">
          {tabs.map(({ id, label, icon: Icon, ready }) => (
            <button
              key={id}
              type="button"
              role="tab"
              aria-selected={tab === id}
              disabled={!ready}
              className={cn(
                'container-modal-tab',
                tab === id && 'container-modal-tab-active',
                !ready && 'container-modal-tab-disabled',
              )}
              onClick={() => ready && setTab(id)}
              title={ready ? undefined : 'Coming in a later version'}
            >
              <Icon className="h-3.5 w-3.5" /> {label}
            </button>
          ))}
        </nav>

        <div className="container-modal-body" role="tabpanel">
          {tab === 'overview' && <OverviewTab guest={guest} connection={connection} isVm={isVm} onClose={onClose} />}
          {tab === 'config' && <ConfigTab connectionId={connection.id} vmId={guest.vmId} kind={kind} readOnly={isVm} />}
          {tab === 'stats' && <StatsTab connectionId={connection.id} vmId={guest.vmId} kind={kind} />}
          {tab === 'tasks' && <TasksTab connectionId={connection.id} vmId={guest.vmId} kind={kind} />}
          {tab === 'watch' && <WatchTab guest={guest} connection={connection} />}
          {/* SSH-backed tabs: kept mounted once opened (see seenLogs/seenConsole)
              so the session survives switching tabs; hidden when not active. */}
          {seenLogs && (
            <div hidden={tab !== 'logs'}>
              <LogsTab guest={guest} connection={connection} active={tab === 'logs'} />
            </div>
          )}
          {seenConsole && (
            <div hidden={tab !== 'console'}>
              <ConsoleTab guest={guest} connection={connection} active={tab === 'console'} />
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ── Overview tab ────────────────────────────────────────────────────────────

function OverviewTab({ guest, connection, isVm, onClose }: { guest: ProxmoxGuest; connection: ProxmoxConnection; isVm: boolean; onClose: () => void }) {
  const mem = formatBytes(guest.memoryBytes)
  const disk = formatBytes(guest.diskBytes)
  const uptime = guest.isRunning ? formatUptime(guest.uptimeSeconds) : null

  return (
    <div className="container-modal-overview">
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">{isVm ? 'Virtual machine' : 'Container'}</h3>
        <dl className="container-modal-summary">
          <dt>VMID</dt>
          <dd><code className="container-modal-code">{guest.vmId}</code></dd>
          <dt>Node</dt>
          <dd>{connection.nodeName}</dd>
          <dt>Host</dt>
          <dd>{connection.name}</dd>
          {guest.ipAddress && (<><dt>IP address</dt><dd><code className="container-modal-code">{guest.ipAddress}</code></dd></>)}
          <dt>Status</dt>
          <dd><ContainerStateBadge state={guest.isRunning ? 'running' : 'stopped'} /></dd>
          {uptime && (<><dt>Uptime</dt><dd>{uptime}</dd></>)}
        </dl>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Resources</h3>
        <dl className="container-modal-summary">
          {guest.cpuCores != null && (<><dt>vCPU</dt><dd>{guest.cpuCores}</dd></>)}
          {mem && (<><dt>Memory</dt><dd>{mem}</dd></>)}
          {disk && (<><dt>Disk</dt><dd>{disk}</dd></>)}
        </dl>
      </section>

      {guest.tags.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Tags</h3>
          <div className="container-modal-card-ports">
            {guest.tags.map((t) => <code key={t} className="container-modal-code">{t}</code>)}
          </div>
        </section>
      )}

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Icon</h3>
        <GuestIconSection connectionId={connection.id} vmId={guest.vmId} name={guest.name} />
      </section>

      <LifecycleSection guest={guest} connection={connection} isVm={isVm} onClose={onClose} />
    </div>
  )
}

// ── Icon management (Overview tab) ───────────────────────────────────────────

/**
 * V7.8 — set or clear a guest's custom card icon. Mirrors the Docker
 * container-icon section: a preview of the current avatar (custom upload →
 * official OS icon → placeholder), an upload button, and a Reset-to-auto button.
 * Both mutations invalidate the icon map so the card updates immediately.
 */
function GuestIconSection({ connectionId, vmId, name }: { connectionId: string; vmId: number; name: string }) {
  const icons = useProxmoxGuestIcons(connectionId)
  const upload = useUploadGuestIcon(connectionId)
  const reset = useResetGuestIcon(connectionId)
  const [error, setError] = useState<string | null>(null)
  const busy = upload.isPending || reset.isPending
  const current = icons.data?.[String(vmId)] ?? null

  const onFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setError(null)
    try {
      await upload.mutateAsync({ vmId, file })
    } catch (err) {
      setError(getApiErrorMessage(err) ?? 'Upload failed')
    }
  }

  const onReset = async () => {
    setError(null)
    try {
      await reset.mutateAsync(vmId)
    } catch (err) {
      setError(getApiErrorMessage(err) ?? 'Reset failed')
    }
  }

  return (
    <div className="container-modal-icon-row">
      <ContainerIcon dataUri={current} name={name} />
      <div className="container-modal-actions">
        <label className="service-modal-upload">
          Upload
          <input type="file" accept="image/*" className="sr-only" disabled={busy} onChange={onFile} />
        </label>
        <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onReset}>
          Reset to auto
        </Button>
      </div>
      {error && (
        <p className="docker-instances-card-action-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
    </div>
  )
}

// ── Linked service (Overview tab) ────────────────────────────────────────────

/**
 * V7.9 — link this guest to a single Stashboard service, the Proxmox analogue of
 * a Docker watch's "Linked service" dropdown. Picking a service (or "standalone")
 * applies immediately; the linked service then shows a Proxmox update badge on its
 * dashboard card. Deep-links to the linked service.
 */
function GuestServiceLinkSection({ connectionId, guest }: { connectionId: string; guest: ProxmoxGuest }) {
  const servicesQuery = useServices()
  const setService = useSetProxmoxGuestService(connectionId)
  const [error, setError] = useState<string | null>(null)

  const services = servicesQuery.data ?? []
  // Group services by category — the same grouping the Services page uses
  // (categoryName → "Uncategorized"), categories A→Z with "Uncategorized" last.
  const servicesByCategory = useMemo(() => {
    const groups = new Map<string, typeof services>()
    for (const s of services) {
      const key = s.categoryName?.trim() || 'Uncategorized'
      const arr = groups.get(key) ?? []
      arr.push(s)
      groups.set(key, arr)
    }
    return [...groups.entries()]
      .map(([name, items]) => [name, [...items].sort((a, b) => a.name.localeCompare(b.name))] as const)
      .sort((a, b) => {
        if (a[0] === 'Uncategorized') return 1
        if (b[0] === 'Uncategorized') return -1
        return a[0].localeCompare(b[0])
      })
  }, [servicesQuery.data])

  // Auto-apply on select — this lives on Overview (no Save button), consistent
  // with the Icon control right above it.
  const onChange = async (value: string) => {
    setError(null)
    try {
      await setService.mutateAsync({ vmId: guest.vmId, webResourceId: value || null })
    } catch (e) {
      setError(getApiErrorMessage(e) ?? 'Failed to update linked service')
    }
  }

  return (
    <div className="container-modal-proxmox-link">
      <div className="docker-connection-picker-row" style={{ alignItems: 'center' }}>
        <select
          className="service-modal-select"
          style={{ flex: 1, minWidth: 0 }}
          value={guest.linkedServiceId ?? ''}
          disabled={setService.isPending || servicesQuery.isLoading}
          onChange={(e) => void onChange(e.target.value)}
        >
          <option value="">— standalone (no service) —</option>
          {servicesByCategory.map(([groupName, items]) => (
            <optgroup key={groupName} label={groupName}>
              {items.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </optgroup>
          ))}
        </select>
        {guest.linkedServiceId && (
          <Link
            to={`/?service=${guest.linkedServiceId}`}
            className="docker-instances-card-watch-link"
            style={{ marginLeft: '0.5rem', flexShrink: 0, whiteSpace: 'nowrap' }}
          >
            <ExternalLink className="h-3 w-3" /> Open service
          </Link>
        )}
      </div>
      <p className="text-xs text-[var(--muted-foreground)] mt-1">
        Link this LXC / VM to a service to show its pending-update status on the service's
        dashboard card. A service can link both Docker containers and Proxmox LXCs / VMs.
      </p>
      {error && (
        <p className="docker-instances-card-action-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
    </div>
  )
}

// ── Lifecycle (shared by the Overview tab) ───────────────────────────────────

function LifecycleSection({ guest, connection, isVm, onClose }: { guest: ProxmoxGuest; connection: ProxmoxConnection; isVm: boolean; onClose: () => void }) {
  const connectionId = connection.id
  const action = useProxmoxLxcAction(connectionId, isVm ? 'qemu' : 'lxc')
  const destroy = useDestroyProxmoxLxc(connectionId, isVm ? 'qemu' : 'lxc')
  const features = useFeatures()
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<ProxmoxLxcAction | null>(null)
  const [destroyOpen, setDestroyOpen] = useState(false)
  // V6.14 — the two power-off verbs go through a confirm dialog that explains the
  // difference (graceful Shutdown vs hard Stop); Start / Reboot run directly.
  const [confirmPower, setConfirmPower] = useState<LxcPowerAction | null>(null)

  // V6.13 — the Destroy affordance only appears when the server-wide switch and
  // the per-host opt-in are both on. A running guest can't be destroyed (stop
  // first), so the button is hidden while running; the server re-checks all of
  // this regardless. V6.14 — works for both LXC containers and QEMU VMs.
  const canDestroy = (features.data?.allowProxmoxDestroy ?? false) && connection.allowDestroy

  const run = (a: ProxmoxLxcAction) => {
    setError(null)
    setDone(null)
    action.mutate(
      { vmId: guest.vmId, action: a },
      {
        onError: (e) => setError(getApiErrorMessage(e) ?? `Failed to ${a} the container`),
        onSuccess: () => setDone(a),
      },
    )
  }

  const busy = action.isPending
  return (
    <section className="container-modal-section">
      <h3 className="container-modal-section-title">Lifecycle</h3>
      <div className="container-modal-actions">
        {guest.isRunning ? (
          <>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setConfirmPower('shutdown')}>
              <Power className="h-3.5 w-3.5" /> Shutdown
            </Button>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => run('reboot')}>
              <RotateCcw className="h-3.5 w-3.5" /> Reboot
            </Button>
            <Button type="button" size="sm" variant="outline" className="ui-button-danger" disabled={busy} onClick={() => setConfirmPower('stop')}>
              <Square className="h-3.5 w-3.5" /> Stop
            </Button>
          </>
        ) : (
          <>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => run('start')}>
              <Play className="h-3.5 w-3.5" /> Start
            </Button>
            {canDestroy && (
              <Button type="button" size="sm" variant="outline" className="ui-button-danger" disabled={busy} onClick={() => setDestroyOpen(true)}>
                <Trash2 className="h-3.5 w-3.5" /> Destroy
              </Button>
            )}
          </>
        )}
        {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
      </div>
      {canDestroy && !guest.isRunning && (
        <p className="container-modal-empty">
          <strong>Destroy</strong> permanently removes this {isVm ? 'VM and its disks' : 'container and its disk'} from the Proxmox host — it cannot be undone.
        </p>
      )}
      {error && (
        <p className="container-modal-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
      {done && !error && (
        <p className="container-modal-empty">
          “{done}” sent. Proxmox is applying it; the card updates right away — reopen this dialog to see the refreshed state here.
        </p>
      )}
      {destroyOpen && (
        <LxcDestroyDialog
          open={destroyOpen}
          vmId={guest.vmId}
          name={guest.name}
          nodeName={connection.nodeName}
          isVm={isVm}
          onConfirm={async () => {
            await destroy.mutateAsync(guest.vmId)
            setDestroyOpen(false)
            onClose()   // the guest is gone — close the modal
          }}
          onCancel={() => setDestroyOpen(false)}
        />
      )}
      {confirmPower && (
        <LxcPowerConfirmDialog
          open
          action={confirmPower}
          vmId={guest.vmId}
          name={guest.name}
          isVm={isVm}
          busy={busy}
          onConfirm={() => { const a = confirmPower; setConfirmPower(null); run(a) }}
          onCancel={() => setConfirmPower(null)}
        />
      )}
    </section>
  )
}

// ── Watch tab (update tracking — the LXC analogue of the Docker Watch tab) ────

function WatchTab({ guest, connection }: { guest: ProxmoxGuest; connection: ProxmoxConnection }) {
  const connectionId = connection.id
  const setMonitoring = useSetProxmoxLxcMonitoring(connectionId)
  const snooze = useSnoozeProxmoxLxc(connectionId)
  const checkNow = useCheckProxmoxLxc(connectionId)
  const features = useFeatures()
  const now = useNowTick()   // so the "Last checked" label ticks instead of freezing
  const [error, setError] = useState<string | null>(null)
  const [updateOpen, setUpdateOpen] = useState(false)

  const sshConfigured = !!(connection.hasSshPrivateKey && connection.sshHost && connection.sshUsername)
  const canApplyUpdates = (features.data?.allowProxmoxUpdates ?? false) && connection.allowUpdates && sshConfigured

  const monitoringOn = guest.monitoringEnabled
  const snoozedUntil = guest.monitoringSnoozedUntil
  const snoozeActive = !!snoozedUntil && new Date(snoozedUntil).getTime() > now
  const sshGap = !!guest.lastError && guest.lastError.startsWith('SSH is not configured')
  const realError = monitoringOn && !!guest.lastError && !sshGap

  const toggle = (enabled: boolean) => {
    setError(null)
    setMonitoring.mutate(
      { vmId: guest.vmId, enabled },
      { onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to update monitoring') },
    )
  }

  const applySnooze = (hours: number | null) => {
    setError(null)
    const until = hours == null ? null : new Date(Date.now() + hours * 3600_000).toISOString()
    snooze.mutate(
      { vmId: guest.vmId, until },
      { onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to snooze monitoring') },
    )
  }

  const runCheck = () => {
    setError(null)
    checkNow.mutate(guest.vmId, {
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to check for updates'),
    })
  }

  return (
    <div className="container-modal-overview">
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Linked service</h3>
        <GuestServiceLinkSection connectionId={connection.id} guest={guest} />
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Monitoring</h3>
        {/* Docker-watch parity: the same checkbox + helper-text pattern as the
            "Enabled" toggle on a Docker watch. */}
        <label className="service-modal-checkbox-label service-modal-label">
          <input
            type="checkbox"
            checked={monitoringOn}
            disabled={setMonitoring.isPending}
            onChange={(e) => toggle(e.target.checked)}
          />
          Monitoring enabled
        </label>
        <p className="container-modal-empty">
          When on, this container is included in the host's scheduled update checks and notifications.
          Turn it off to skip a noisy or intentionally unmanaged container without disabling the whole host —
          the same as pausing a Docker watch.
        </p>
        {!monitoringOn && (
          <p className="container-modal-empty">
            Monitoring is off — this container is skipped by scheduled and manual checks and excluded from
            update notifications.
          </p>
        )}
        {error && (
          <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {error}</p>
        )}
      </section>

      {/* V6.11 — maintenance-window snooze: temporarily skip checks without
          permanently disabling monitoring; the host auto-re-includes the guest
          once the window passes. */}
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Maintenance snooze</h3>
        {snoozeActive ? (
          <>
            <p className="container-modal-empty">
              Snoozed until <strong>{new Date(snoozedUntil!).toLocaleString()}</strong> ({relativeTime(snoozedUntil, now)}).
              Checks and notifications resume automatically after that.
            </p>
            <div className="container-modal-actions">
              <Button type="button" size="sm" variant="outline" disabled={snooze.isPending}
                onClick={() => applySnooze(null)}>
                Clear snooze
              </Button>
            </div>
          </>
        ) : (
          <>
            <p className="container-modal-empty">
              Snooze this container for a maintenance window — it's skipped by scheduled and manual checks until the
              window ends, then re-included automatically (monitoring stays on).
            </p>
            <div className="container-modal-actions">
              {([['1h', 1], ['6h', 6], ['24h', 24], ['7d', 24 * 7]] as const).map(([label, hours]) => (
                <Button key={label} type="button" size="sm" variant="outline" disabled={snooze.isPending}
                  onClick={() => applySnooze(hours)}>
                  {label}
                </Button>
              ))}
            </div>
          </>
        )}
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Update tracking</h3>
        <dl className="container-modal-summary">
          <dt>Pending</dt>
          <dd>
            {!monitoringOn
              ? 'monitoring off'
              : guest.pendingUpdates == null
                ? (sshGap ? 'n/a — SSH not configured' : 'unknown')
                : guest.pendingUpdates > 0
                  ? `${guest.pendingUpdates} update${guest.pendingUpdates === 1 ? '' : 's'}`
                  : 'Up to date'}
          </dd>
          <dt>Last checked</dt>
          <dd>{relativeTime(guest.lastCheckedUtc, now)}</dd>
        </dl>
        {realError && (
          <p className="container-modal-error">
            <AlertCircle className="h-3.5 w-3.5 inline" /> {guest.lastError}
          </p>
        )}
        {monitoringOn && sshGap && (
          <p className="container-modal-empty">Add SSH to this host (Edit) to read its update count.</p>
        )}
        <div className="container-modal-actions">
          <Button type="button" size="sm" variant="outline" disabled={checkNow.isPending} onClick={runCheck}>
            <RefreshCw className={cn('h-3.5 w-3.5', checkNow.isPending && 'animate-spin')} /> Check now
          </Button>
          {canApplyUpdates && (
            <Button type="button" size="sm" onClick={() => setUpdateOpen(true)}>
              <RefreshCw className="h-3.5 w-3.5" /> Update now
            </Button>
          )}
        </div>
        <p className="container-modal-empty">
          Proxmox has no per-container probe — a check is a single sweep of the whole node, so
          <strong> Check now</strong> re-scans <strong>every</strong> container on this node at once, not just this one.
          {canApplyUpdates && <> <strong>Update now</strong> applies the pending updates inside this container via <code>apt-get dist-upgrade</code>.</>}
        </p>
        {canApplyUpdates && (
          <div className="service-modal-field service-modal-field-full mt-2">
            <ApplyCommandPanel connectionId={connectionId} vmId={guest.vmId} />
          </div>
        )}
        {updateOpen && (
          <ProxmoxUpdateDialog
            connectionId={connectionId}
            vmId={guest.vmId}
            targetLabel={`${guest.name} (CT ${guest.vmId})`}
            onClose={() => setUpdateOpen(false)}
          />
        )}
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Schedule &amp; notifications</h3>
        <p className="container-modal-empty">
          Update checks and notifications for this LXC run on the host's schedule. Use
          {' '}<strong>Edit</strong> on the Proxmox host to change the cadence, email, or Telegram alerts.
        </p>
      </section>
    </div>
  )
}

// ── Update command panel (the LXC analogue of the Docker Watch tab's
//    "Update command" field — same markup, same copy affordance) ────────────

function ApplyCommandPanel({ connectionId, vmId }: { connectionId: string; vmId: number }) {
  const query = useProxmoxUpdateCommand(connectionId, vmId)
  const [copied, setCopied] = useState(false)

  const copy = () => {
    if (!query.data) return
    void navigator.clipboard.writeText(query.data).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  if (query.isLoading) {
    return (
      <>
        <Label className="service-modal-label">Update command</Label>
        <p className="text-sm text-[var(--muted-foreground)]">Generating…</p>
      </>
    )
  }
  if (query.error || !query.data) {
    return (
      <>
        <Label className="service-modal-label">Update command</Label>
        <p className="docker-test-result-error">{getApiErrorMessage(query.error) ?? 'Failed to generate command'}</p>
      </>
    )
  }

  return (
    <>
      <Label className="service-modal-label">
        Update command <span className="service-modal-label-help">(runs on the host over SSH)</span>
      </Label>
      <div className="docker-copy-command">
        <pre>{query.data}</pre>
        <button type="button" className="service-modal-copy-btn" onClick={copy} title="Copy command">
          {copied ? <Check className="h-3.5 w-3.5 service-modal-copy-success" /> : <Copy className="h-3.5 w-3.5" />}
        </button>
      </div>
    </>
  )
}

// ── Console tab (V6.6 — browser SSH shell into the LXC, the LXC analogue of
//    the Docker container Exec tab) ─────────────────────────────────────────

function ConsoleTab({ guest, connection, active }: { guest: ProxmoxGuest; connection: ProxmoxConnection; active: boolean }) {
  const features = useFeatures()
  const sshConfigured = !!(connection.hasSshPrivateKey && connection.sshHost && connection.sshUsername)
  return (
    <LxcConsolePanel
      connectionId={connection.id}
      vmId={guest.vmId}
      isRunning={guest.isRunning}
      sshConfigured={sshConfigured}
      allowConsole={connection.allowConsole}
      allowConsoleGlobal={features.data?.allowProxmoxConsole ?? false}
      active={active}
    />
  )
}

// ── Logs tab (V6.12 — read-only live journal tail, the LXC analogue of the
//    Docker container Logs tab) ────────────────────────────────────────────────

function LogsTab({ guest, connection, active }: { guest: ProxmoxGuest; connection: ProxmoxConnection; active: boolean }) {
  const features = useFeatures()
  const sshConfigured = !!(connection.hasSshPrivateKey && connection.sshHost && connection.sshUsername)
  return (
    <LxcLogsPanel
      connectionId={connection.id}
      vmId={guest.vmId}
      isRunning={guest.isRunning}
      sshConfigured={sshConfigured}
      allowConsole={connection.allowConsole}
      allowConsoleGlobal={features.data?.allowProxmoxConsole ?? false}
      active={active}
    />
  )
}

// ── Config tab ──────────────────────────────────────────────────────────────

function ConfigTab({ connectionId, vmId, kind, readOnly }: { connectionId: string; vmId: number; kind: ProxmoxGuestKind; readOnly: boolean }) {
  const query = useProxmoxLxcConfig(connectionId, vmId, true, kind)
  const [editing, setEditing] = useState(false)

  if (query.isLoading) return <p className="container-modal-empty">Loading configuration from Proxmox…</p>
  if (query.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read configuration'}
      </p>
    )
  }
  const d = query.data
  if (!d) return <p className="container-modal-empty">No configuration.</p>

  // V6.14 — VM config is read-only for now (its virtio/scsi/PCI-passthrough
  // surface is a much larger structured-edit problem than the LXC fields).
  if (editing && !readOnly) {
    return (
      <EditConfigForm connectionId={connectionId} vmId={vmId} detail={d} onDone={() => setEditing(false)} />
    )
  }
  return <ConfigReadView detail={d} isVm={readOnly} onEdit={readOnly ? undefined : () => setEditing(true)} />
}

function ConfigReadView({ detail: d, isVm, onEdit }: { detail: ProxmoxLxcDetail; isVm: boolean; onEdit?: () => void }) {
  const yesNo = (v: boolean | null) => (v == null ? '—' : v ? 'yes' : 'no')
  const cpuPct = d.cpuFraction != null ? `${(d.cpuFraction * 100).toFixed(1)}%` : null
  const memUsed = formatBytes(d.memUsedBytes)
  const memMax = formatBytes(d.memMaxBytes ?? d.memoryBytes)
  const diskUsed = formatBytes(d.diskUsedBytes)
  const diskMax = formatBytes(d.diskMaxBytes)
  const uptime = formatUptime(d.uptimeSeconds)

  return (
    <div className="container-modal-overview">
      <div className="container-modal-stats-header">
        <span className="container-modal-empty">
          Read-only snapshot from Proxmox.{isVm && ' VM configuration is read-only here.'}
        </span>
        {onEdit && (
          <Button type="button" size="sm" variant="outline" onClick={onEdit}>
            <Pencil className="h-3.5 w-3.5" /> Edit
          </Button>
        )}
      </div>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Resources</h3>
        <dl className="container-modal-summary">
          {d.cores != null && (<><dt>Cores</dt><dd>{d.cores} vCPU</dd></>)}
          {d.memoryBytes != null && (<><dt>Memory</dt><dd>{formatBytes(d.memoryBytes)}</dd></>)}
          {d.swapBytes != null && (<><dt>Swap</dt><dd>{formatBytes(d.swapBytes)}</dd></>)}
          {cpuPct && (<><dt>CPU now</dt><dd>{cpuPct}</dd></>)}
          {(memUsed || memMax) && (<><dt>Memory used</dt><dd>{memUsed ?? '—'}{memMax ? ` / ${memMax}` : ''}</dd></>)}
          {(diskUsed || diskMax) && (<><dt>Disk used</dt><dd>{diskUsed ?? '—'}{diskMax ? ` / ${diskMax}` : ''}</dd></>)}
          {uptime && (<><dt>Uptime</dt><dd>{uptime}</dd></>)}
        </dl>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">System</h3>
        <dl className="container-modal-summary">
          {d.hostname && (<><dt>{isVm ? 'Name' : 'Hostname'}</dt><dd><code className="container-modal-code">{d.hostname}</code></dd></>)}
          {d.osType && (<><dt>OS type</dt><dd>{d.osType}</dd></>)}
          {d.arch && (<><dt>Arch</dt><dd>{d.arch}</dd></>)}
          <dt>Start at boot</dt><dd>{yesNo(d.onboot)}</dd>
          {/* Unprivileged / features are LXC-only — a VM leaves them null. */}
          {!isVm && (<><dt>Unprivileged</dt><dd>{yesNo(d.unprivileged)}</dd></>)}
          {d.features && (<><dt>Features</dt><dd><code className="container-modal-code">{d.features}</code></dd></>)}
        </dl>
      </section>

      {d.mounts.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">{isVm ? 'Disks' : 'Mount points'}</h3>
          <ConfigLineList lines={d.mounts} />
        </section>
      )}

      {d.networks.length > 0 && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Network</h3>
          <ConfigLineList lines={d.networks} />
        </section>
      )}
    </div>
  )
}

// ── Config edit (V6.5 scalars + V6.9 net / mp / rootfs) ──────────────────────

const BYTES_PER_MIB = 1024 * 1024

/** How a change takes effect — drives the review/confirm impact label. V6.9 adds
 *  'destructive' for removals (which Proxmox routes through `delete=`). For
 *  ambiguous structured edits we deliberately pick the stricter label. */
type ApplyKind = 'live' | 'restart' | 'nextboot' | 'destructive'

/** One line in the pre-save review list. */
interface ReviewItem { label: string; from?: string; to: string; apply: ApplyKind; note?: string }

/** A net<n> row in the editor: its identity, the normalised original line (null
 *  for a freshly added row), the working model, and per-row UI flags. */
interface NetRow { uid: string; key: string; origNorm: string | null; model: ProxmoxLxcNetChange; removed: boolean; advanced: boolean }
interface MountRow { uid: string; key: string; origNorm: string | null; model: ProxmoxLxcMountChange; removed: boolean; advanced: boolean }

let rowSeq = 0
const nextUid = () => `lxc-row-${rowSeq++}`

function bytesToMib(bytes: number | null): string {
  return bytes == null ? '' : String(Math.round(bytes / BYTES_PER_MIB))
}

// ── client-side validation (mirrors the backend ProxmoxLxcConfigValidator;
//    the server stays authoritative and any host rejection is surfaced verbatim) ──
const MAC_RE = /^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$/
const SIZE_RE = /^[0-9]+(\.[0-9]+)?[TGMK]?$/i

function isIpv4(v: string): boolean {
  const parts = v.trim().split('.')
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255)
}
function isIpv6(v: string): boolean {
  return /^[0-9a-fA-F:]+$/.test(v.trim()) && v.includes(':')
}
function validIpMode(v: string, ipv6: boolean): boolean {
  const t = v.trim()
  if (t === '' || t === 'dhcp' || t === 'manual' || (ipv6 && t === 'auto')) return true
  const slash = t.indexOf('/')
  if (slash < 0) return false
  const addr = t.slice(0, slash)
  const prefix = Number(t.slice(slash + 1))
  if (!Number.isInteger(prefix)) return false
  return ipv6 ? isIpv6(addr) && prefix >= 0 && prefix <= 128 : isIpv4(addr) && prefix >= 0 && prefix <= 32
}

/** The editable Config form. Scalars (V6.5) sit alongside the V6.9 structured
 *  net / mp / rootfs row editors. The user edits, hits "Review changes", sees a
 *  per-change impact list (with destructive removals called out by exact key),
 *  confirms, then the single PUT fires. */
function EditConfigForm({ connectionId, vmId, detail: d, onDone }: {
  connectionId: string
  vmId: number
  detail: ProxmoxLxcDetail
  onDone: () => void
}) {
  const mutation = useUpdateProxmoxLxcConfig(connectionId, vmId)
  const running = d.status === 'running'

  // ── scalar state (V6.5) ──
  const [cores, setCores] = useState(d.cores != null ? String(d.cores) : '')
  const [memory, setMemory] = useState(bytesToMib(d.memoryBytes))
  const [swap, setSwap] = useState(bytesToMib(d.swapBytes))
  const [hostname, setHostname] = useState(d.hostname ?? '')
  const [onboot, setOnboot] = useState(d.onboot ?? false)

  // ── structured state (V6.9): rootfs is separated out of the mount list ──
  const rootfsLine = useMemo(() => d.mounts.find((m) => m.key === 'rootfs') ?? null, [d])
  const rootfsOrigNorm = useMemo(() => (rootfsLine ? formatRootfs(parseRootfs(rootfsLine.value)) : null), [rootfsLine])

  const [nets, setNets] = useState<NetRow[]>(() =>
    d.networks.map((l) => {
      const model = parseNet(l.key, l.value)
      return { uid: nextUid(), key: l.key, origNorm: formatNet(model), model, removed: false, advanced: false }
    }))
  const [mounts, setMounts] = useState<MountRow[]>(() =>
    d.mounts.filter((m) => m.key !== 'rootfs').map((l) => {
      const model = parseMount(l.key, l.value)
      return { uid: nextUid(), key: l.key, origNorm: formatMount(model), model, removed: false, advanced: false }
    }))
  const [rootfs, setRootfs] = useState<ProxmoxLxcRootfsChange>(() =>
    rootfsLine ? parseRootfs(rootfsLine.value) : {})
  const [rootfsAdvanced, setRootfsAdvanced] = useState(false)

  const [confirming, setConfirming] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // ── net/mp mutators ──
  const patchNet = (uid: string, patch: Partial<ProxmoxLxcNetChange>) =>
    setNets((rows) => rows.map((r) => (r.uid === uid ? { ...r, model: { ...r.model, ...patch } } : r)))
  const setNetFlag = (uid: string, patch: Partial<Pick<NetRow, 'removed' | 'advanced'>>) =>
    setNets((rows) => rows.map((r) => (r.uid === uid ? { ...r, ...patch } : r)))
  const setNetRaw = (uid: string, raw: string) =>
    setNets((rows) => rows.map((r) => (r.uid === uid ? { ...r, model: { ...r.model, raw } } : r)))
  const removeNetRow = (uid: string) =>
    setNets((rows) => rows.filter((r) => r.uid !== uid))   // drop a never-saved (new) row entirely
  const addNet = () =>
    setNets((rows) => [...rows, {
      uid: nextUid(), key: '', origNorm: null,
      model: { key: '', name: `eth${rows.length}`, bridge: 'vmbr0', ip: 'dhcp' }, removed: false, advanced: false,
    }])

  const patchMount = (uid: string, patch: Partial<ProxmoxLxcMountChange>) =>
    setMounts((rows) => rows.map((r) => (r.uid === uid ? { ...r, model: { ...r.model, ...patch } } : r)))
  const setMountFlag = (uid: string, patch: Partial<Pick<MountRow, 'removed' | 'advanced'>>) =>
    setMounts((rows) => rows.map((r) => (r.uid === uid ? { ...r, ...patch } : r)))
  const setMountRaw = (uid: string, raw: string) =>
    setMounts((rows) => rows.map((r) => (r.uid === uid ? { ...r, model: { ...r.model, raw } } : r)))
  const removeMountRow = (uid: string) =>
    setMounts((rows) => rows.filter((r) => r.uid !== uid))
  const addMount = () =>
    setMounts((rows) => [...rows, {
      uid: nextUid(), key: '', origNorm: null,
      model: { key: '', volume: '', mountPoint: '', size: '8G' }, removed: false, advanced: false,
    }])

  // Original scalar values, normalised the same way the inputs are.
  const original = useMemo(() => ({
    cores: d.cores != null ? String(d.cores) : '',
    memory: bytesToMib(d.memoryBytes),
    swap: bytesToMib(d.swapBytes),
    hostname: d.hostname ?? '',
    onboot: d.onboot ?? false,
  }), [d])

  // Build the update payload + the review list together, so what the user
  // confirms is exactly what gets sent.
  const { update, review } = useMemo(() => {
    const update: ProxmoxLxcConfigUpdate = {}
    const review: ReviewItem[] = []

    if (cores.trim() !== original.cores) { update.cores = cores.trim() === '' ? null : Number(cores); review.push({ label: 'Cores', from: original.cores || '—', to: cores.trim() || '—', apply: 'live' }) }
    if (memory.trim() !== original.memory) { update.memoryMib = memory.trim() === '' ? null : Number(memory); review.push({ label: 'Memory (MiB)', from: original.memory || '—', to: memory.trim() || '—', apply: 'live' }) }
    if (swap.trim() !== original.swap) { update.swapMib = swap.trim() === '' ? null : Number(swap); review.push({ label: 'Swap (MiB)', from: original.swap || '—', to: swap.trim() || '—', apply: 'live' }) }
    if (hostname.trim() !== original.hostname) { update.hostname = hostname.trim(); review.push({ label: 'Hostname', from: original.hostname || '—', to: hostname.trim() || '—', apply: 'restart' }) }
    if (onboot !== original.onboot) { update.onboot = onboot; review.push({ label: 'Start at boot', from: original.onboot ? 'yes' : 'no', to: onboot ? 'yes' : 'no', apply: 'nextboot' }) }

    const netOps: ProxmoxLxcNetChange[] = []
    for (const r of nets) {
      if (r.origNorm === null) {                      // newly added
        if (r.removed) continue
        netOps.push({ ...r.model, key: '' })
        review.push({ label: 'New interface', to: formatNet(r.model) || '(empty)', apply: 'restart' })
      } else if (r.removed) {
        netOps.push({ key: r.key, remove: true })
        review.push({ label: r.key, to: 'removed', apply: 'destructive', note: `Interface ${r.key} will be deleted from the config (delete=${r.key}).` })
      } else if (formatNet(r.model) !== r.origNorm) {
        netOps.push({ ...r.model, key: r.key })
        review.push({ label: r.key, from: r.origNorm, to: formatNet(r.model), apply: 'restart' })
      }
    }
    if (netOps.length) update.networks = netOps

    const mpOps: ProxmoxLxcMountChange[] = []
    for (const r of mounts) {
      if (r.origNorm === null) {
        if (r.removed) continue
        mpOps.push({ ...r.model, key: '' })
        review.push({ label: 'New mount', to: formatMount(r.model) || '(empty)', apply: 'restart' })
      } else if (r.removed) {
        mpOps.push({ key: r.key, remove: true })
        review.push({ label: r.key, to: 'removed', apply: 'destructive', note: `Mount ${r.key} will be removed from the config (delete=${r.key}). The underlying storage content is NOT deleted.` })
      } else if (formatMount(r.model) !== r.origNorm) {
        mpOps.push({ ...r.model, key: r.key })
        review.push({ label: r.key, from: r.origNorm, to: formatMount(r.model), apply: 'restart' })
      }
    }
    if (mpOps.length) update.mounts = mpOps

    if (rootfsOrigNorm !== null && formatRootfs(rootfs) !== rootfsOrigNorm) {
      update.rootfs = rootfs
      review.push({ label: 'rootfs', from: rootfsOrigNorm, to: formatRootfs(rootfs), apply: 'restart' })
    }

    return { update, review }
  }, [cores, memory, swap, hostname, onboot, original, nets, mounts, rootfs, rootfsOrigNorm])

  // Light client-side validation: scalar [Range] guards + structured guards.
  const errors = useMemo(() => {
    const errs: string[] = []
    const n = (s: string) => (s.trim() === '' ? null : Number(s))
    const c = n(cores), m = n(memory), s = n(swap)
    if (c != null && (!Number.isInteger(c) || c < 1 || c > 8192)) errs.push('Cores must be a whole number between 1 and 8192.')
    if (m != null && (!Number.isInteger(m) || m < 16)) errs.push('Memory must be at least 16 MiB.')
    if (s != null && (!Number.isInteger(s) || s < 0)) errs.push('Swap must be 0 MiB or more.')

    const names = new Set<string>()
    for (const r of nets) {
      if (r.removed || r.advanced || r.model.raw) continue
      const x = r.model
      const lbl = r.origNorm === null ? 'New interface' : r.key
      if (x.name?.trim()) { if (names.has(x.name.trim())) errs.push(`Duplicate interface name '${x.name}'.`); else names.add(x.name.trim()) }
      if (x.ip && !validIpMode(x.ip, false)) errs.push(`${lbl}: '${x.ip}' is not dhcp, manual, or a valid IPv4 CIDR.`)
      if (x.gw?.trim() && !isIpv4(x.gw)) errs.push(`${lbl}: gateway '${x.gw}' is not a valid IPv4 address.`)
      if (x.ip6 && !validIpMode(x.ip6, true)) errs.push(`${lbl}: '${x.ip6}' is not a valid IPv6 mode/CIDR.`)
      if (x.gw6?.trim() && !isIpv6(x.gw6)) errs.push(`${lbl}: IPv6 gateway '${x.gw6}' is not a valid IPv6 address.`)
      if (x.hwaddr?.trim() && !MAC_RE.test(x.hwaddr.trim())) errs.push(`${lbl}: '${x.hwaddr}' is not a valid MAC address.`)
      if (x.tag != null && (x.tag < 1 || x.tag > 4094)) errs.push(`${lbl}: VLAN tag must be between 1 and 4094.`)
      if (x.mtu != null && (x.mtu < 64 || x.mtu > 65520)) errs.push(`${lbl}: MTU must be between 64 and 65520.`)
    }

    const paths = new Set<string>()
    for (const r of mounts) {
      if (r.removed || r.advanced || r.model.raw) continue
      const x = r.model
      const lbl = r.origNorm === null ? 'New mount' : r.key
      const mp = x.mountPoint?.trim() ?? ''
      if (mp === '') errs.push(`${lbl}: a container mount path is required.`)
      else if (!mp.startsWith('/')) errs.push(`${lbl}: mount path '${mp}' must be absolute (start with '/').`)
      else if (paths.has(mp)) errs.push(`Duplicate mount path '${mp}'.`)
      else paths.add(mp)
      if (x.size?.trim() && !SIZE_RE.test(x.size.trim())) errs.push(`${lbl}: '${x.size}' is not a valid size (e.g. 8G).`)
    }

    if (!rootfsAdvanced && !rootfs.raw && rootfs.size?.trim() && !SIZE_RE.test(rootfs.size.trim()))
      errs.push(`rootfs: '${rootfs.size}' is not a valid size (e.g. 8G).`)

    return errs
  }, [cores, memory, swap, nets, mounts, rootfs, rootfsAdvanced])

  const apply = () => {
    setError(null)
    mutation.mutate(update, {
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to update configuration'),
      onSuccess: () => { setConfirming(false); setSaved(true) },
    })
  }

  const busy = mutation.isPending

  // ── saved: surface restart guidance inline before returning to the read view ──
  if (saved) {
    const needsRestart = running && review.some((r) => r.apply === 'restart' || r.apply === 'destructive')
    return (
      <div className="container-modal-overview">
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Saved</h3>
          <p className="container-modal-empty">
            <CheckCircle2 className="h-3.5 w-3.5 inline" /> Proxmox accepted the configuration changes.
          </p>
          {needsRestart && (
            <p className="container-modal-empty">
              <TriangleAlert className="h-3.5 w-3.5 inline" /> This container is running — a <strong>restart</strong> may be
              required for the new network / mount / rootfs config to fully take effect.
            </p>
          )}
          <div className="container-modal-actions">
            <Button type="button" size="sm" onClick={onDone}>Done</Button>
          </div>
        </section>
      </div>
    )
  }

  // ── confirm: per-change review with conservative impact labels ──
  if (confirming) {
    const destructive = review.filter((r) => r.apply === 'destructive')
    const hasRestart = review.some((r) => r.apply === 'restart')
    return (
      <div className="container-modal-overview">
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Review {review.length} change{review.length === 1 ? '' : 's'}</h3>
          <dl className="container-modal-summary">
            {review.map((c, i) => (
              <Fragment key={`${c.label}-${i}`}>
                <dt>{c.label}</dt>
                <dd>
                  {c.from != null && <>{c.from} → </>}
                  <strong>{c.to}</strong>{' '}
                  <span className="container-modal-empty">({applyHint(c.apply)})</span>
                </dd>
              </Fragment>
            ))}
          </dl>
          {destructive.length > 0 && (
            <div className="container-modal-error">
              <p><TriangleAlert className="h-3.5 w-3.5 inline" /> Destructive changes:</p>
              <ul style={{ margin: '0.25rem 0 0', paddingLeft: '1.1rem' }}>
                {destructive.map((c, i) => <li key={i}>{c.note ?? c.label}</li>)}
              </ul>
            </div>
          )}
          {hasRestart && running && (
            <p className="container-modal-empty">
              Some changes may need a container <strong>restart</strong> to fully apply (the guest is running).
            </p>
          )}
          {error && <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {error}</p>}
          <div className="container-modal-actions">
            <Button type="button" size="sm" disabled={busy} onClick={apply}>
              {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />} Apply
            </Button>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setConfirming(false)}>Back</Button>
          </div>
        </section>
      </div>
    )
  }

  return (
    <div className="container-modal-overview">
      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Edit resources</h3>
        <div className="container-modal-edit-grid">
          <label className="service-modal-label">
            Cores (vCPU)
            <Input type="number" min={1} max={8192} value={cores} onChange={(e) => setCores(e.target.value)} placeholder="e.g. 2" />
          </label>
          <label className="service-modal-label">
            Memory (MiB)
            <Input type="number" min={16} value={memory} onChange={(e) => setMemory(e.target.value)} placeholder="e.g. 2048" />
          </label>
          <label className="service-modal-label">
            Swap (MiB)
            <Input type="number" min={0} value={swap} onChange={(e) => setSwap(e.target.value)} placeholder="e.g. 512" />
          </label>
        </div>
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Edit system</h3>
        <div className="container-modal-edit-grid">
          <label className="service-modal-label">
            Hostname
            <Input value={hostname} onChange={(e) => setHostname(e.target.value)} placeholder="e.g. wireguard" />
          </label>
        </div>
        <label className="service-modal-checkbox-label service-modal-label mt-2">
          <input type="checkbox" checked={onboot} onChange={(e) => setOnboot(e.target.checked)} /> Start at boot
        </label>
      </section>

      <section className="container-modal-section">
        <div className="lxc-config-row-header">
          <h3 className="container-modal-section-title"><Network className="h-3.5 w-3.5 inline" /> Network interfaces</h3>
          <Button type="button" size="sm" variant="outline" onClick={addNet}><Plus className="h-3.5 w-3.5" /> Add</Button>
        </div>
        <div className="lxc-config-rows">
          {nets.length === 0 && <p className="container-modal-empty">No network interfaces.</p>}
          {nets.map((r) => (
            <NetRowCard
              key={r.uid}
              row={r}
              onPatch={(p) => patchNet(r.uid, p)}
              onToggleRemove={() => (r.origNorm === null ? removeNetRow(r.uid) : setNetFlag(r.uid, { removed: !r.removed }))}
              onSetAdvanced={(adv) => setNetFlag(r.uid, { advanced: adv })}
              onSetRaw={(raw) => setNetRaw(r.uid, raw)}
            />
          ))}
        </div>
      </section>

      <section className="container-modal-section">
        <div className="lxc-config-row-header">
          <h3 className="container-modal-section-title"><HardDrive className="h-3.5 w-3.5 inline" /> Mount points</h3>
          <Button type="button" size="sm" variant="outline" onClick={addMount}><Plus className="h-3.5 w-3.5" /> Add</Button>
        </div>
        <div className="lxc-config-rows">
          {mounts.length === 0 && <p className="container-modal-empty">No secondary mount points.</p>}
          {mounts.map((r) => (
            <MountRowCard
              key={r.uid}
              row={r}
              onPatch={(p) => patchMount(r.uid, p)}
              onToggleRemove={() => (r.origNorm === null ? removeMountRow(r.uid) : setMountFlag(r.uid, { removed: !r.removed }))}
              onSetAdvanced={(adv) => setMountFlag(r.uid, { advanced: adv })}
              onSetRaw={(raw) => setMountRaw(r.uid, raw)}
            />
          ))}
        </div>
      </section>

      {rootfsLine && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title"><HardDrive className="h-3.5 w-3.5 inline" /> Root filesystem (rootfs)</h3>
          <RootfsCard
            model={rootfs}
            advanced={rootfsAdvanced}
            onPatch={(p) => setRootfs((prev) => ({ ...prev, ...p }))}
            onSetAdvanced={setRootfsAdvanced}
            onSetRaw={(raw) => setRootfs((prev) => ({ ...prev, raw }))}
          />
          <p className="container-modal-empty">rootfs cannot be removed — only modified. Storage migration is out of scope.</p>
        </section>
      )}

      {errors.length > 0 && (
        <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {errors[0]}</p>
      )}

      <div className="container-modal-actions">
        <Button type="button" size="sm" disabled={review.length === 0 || errors.length > 0} onClick={() => { setError(null); setConfirming(true) }}>
          Review changes{review.length > 0 ? ` (${review.length})` : ''}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onDone}>Cancel</Button>
      </div>
    </div>
  )
}

function applyHint(kind: ApplyKind): string {
  switch (kind) {
    case 'live': return 'applies live'
    case 'restart': return 'restart likely'
    case 'nextboot': return 'next boot'
    case 'destructive': return 'destructive / remove'
  }
}

// ── Structured row editors (V6.9) ────────────────────────────────────────────

/** A labelled text/number input matching the V6.5 scalar field style. */
function LxcField({ label, value, onChange, placeholder, type = 'text', help }: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
  type?: string
  help?: string
}) {
  return (
    <label className="service-modal-label">
      {label}{help && <span className="service-modal-label-help"> {help}</span>}
      <Input type={type} value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} />
    </label>
  )
}

function LxcCheck({ label, checked, onChange }: { label: string; checked: boolean; onChange: (c: boolean) => void }) {
  return (
    <label className="service-modal-checkbox-label service-modal-label">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} /> {label}
    </label>
  )
}

/** The raw/details expander every row carries: shows the *exact* line the server
 *  will generate, with a one-click switch into advanced raw editing. */
function RawExpander({ generated, onEditRaw }: { generated: string; onEditRaw: () => void }) {
  return (
    <details className="container-modal-collapsible">
      <summary className="container-modal-collapsible-with-action">
        <span className="task-type-row">Raw option string</span>
      </summary>
      <code className="container-modal-code container-modal-code-block lxc-config-raw">{generated || '(empty)'}</code>
      <div className="container-modal-actions">
        <Button type="button" size="sm" variant="outline" onClick={onEditRaw}>Edit as raw</Button>
      </div>
    </details>
  )
}

const numOrNull = (v: string): number | null => (v.trim() === '' ? null : Number(v))

function NetRowCard({ row, onPatch, onToggleRemove, onSetAdvanced, onSetRaw }: {
  row: NetRow
  onPatch: (patch: Partial<ProxmoxLxcNetChange>) => void
  onToggleRemove: () => void
  onSetAdvanced: (adv: boolean) => void
  onSetRaw: (raw: string) => void
}) {
  const m = row.model
  const isNew = row.origNorm === null
  const generated = formatNet(m)

  return (
    <div className={cn('lxc-config-row', row.removed && 'lxc-config-row-removed')}>
      <div className="lxc-config-row-header">
        <span className="lxc-config-row-key">{isNew ? 'New interface' : row.key}{m.name ? ` · ${m.name}` : ''}</span>
        <Button type="button" size="sm" variant="outline" className={cn(!row.removed && 'ui-button-danger')} onClick={onToggleRemove}>
          {row.removed ? 'Restore' : <><Trash2 className="h-3.5 w-3.5" /> Remove</>}
        </Button>
      </div>

      {row.removed ? (
        <p className="container-modal-empty">{isNew ? 'Discarded.' : `${row.key} will be deleted from the config (delete=${row.key}).`}</p>
      ) : row.advanced ? (
        <>
          <label className="service-modal-label">
            Raw option string
            <Textarea value={m.raw ?? generated} rows={2} onChange={(e) => onSetRaw(e.target.value)} />
          </label>
          <div className="container-modal-actions">
            <Button type="button" size="sm" variant="outline" onClick={() => onSetAdvanced(false)}>Back to structured</Button>
          </div>
        </>
      ) : (
        <>
          <div className="container-modal-edit-grid">
            <LxcField label="Name" value={m.name ?? ''} onChange={(v) => onPatch({ name: v })} placeholder="eth0" />
            <LxcField label="Bridge" value={m.bridge ?? ''} onChange={(v) => onPatch({ bridge: v })} placeholder="vmbr0" />
            <LxcField label="IPv4" value={m.ip ?? ''} onChange={(v) => onPatch({ ip: v })} placeholder="dhcp / manual / 10.0.0.5/24" />
            <LxcField label="Gateway" value={m.gw ?? ''} onChange={(v) => onPatch({ gw: v })} placeholder="10.0.0.1" />
            <LxcField label="IPv6" value={m.ip6 ?? ''} onChange={(v) => onPatch({ ip6: v })} placeholder="dhcp / auto / manual / CIDR" />
            <LxcField label="IPv6 gateway" value={m.gw6 ?? ''} onChange={(v) => onPatch({ gw6: v })} placeholder="fe80::1" />
            <LxcField label="VLAN tag" type="number" value={m.tag == null ? '' : String(m.tag)} onChange={(v) => onPatch({ tag: numOrNull(v) })} placeholder="—" />
            <LxcField label="MTU" type="number" value={m.mtu == null ? '' : String(m.mtu)} onChange={(v) => onPatch({ mtu: numOrNull(v) })} placeholder="1500" />
            <LxcField label="Rate (MB/s)" type="number" value={m.rate == null ? '' : String(m.rate)} onChange={(v) => onPatch({ rate: numOrNull(v) })} placeholder="—" />
            <LxcField label="MAC" value={m.hwaddr ?? ''} onChange={(v) => onPatch({ hwaddr: v })} placeholder="auto" />
          </div>
          <div className="container-modal-actions">
            <LxcCheck label="Firewall" checked={!!m.firewall} onChange={(c) => onPatch({ firewall: c })} />
            <LxcCheck label="Disabled (link down)" checked={!!m.linkDown} onChange={(c) => onPatch({ linkDown: c })} />
          </div>
          {hasUnknownOptions(m.extra) && (
            <p className="container-modal-empty">
              <TriangleAlert className="h-3.5 w-3.5 inline" /> Options Stashboard doesn't model are preserved: <code className="container-modal-code">{m.extra}</code>
            </p>
          )}
          <RawExpander generated={generated} onEditRaw={() => { onSetRaw(generated); onSetAdvanced(true) }} />
        </>
      )}
    </div>
  )
}

function MountRowCard({ row, onPatch, onToggleRemove, onSetAdvanced, onSetRaw }: {
  row: MountRow
  onPatch: (patch: Partial<ProxmoxLxcMountChange>) => void
  onToggleRemove: () => void
  onSetAdvanced: (adv: boolean) => void
  onSetRaw: (raw: string) => void
}) {
  const m = row.model
  const isNew = row.origNorm === null
  const generated = formatMount(m)

  return (
    <div className={cn('lxc-config-row', row.removed && 'lxc-config-row-removed')}>
      <div className="lxc-config-row-header">
        <span className="lxc-config-row-key">{isNew ? 'New mount' : row.key}{m.mountPoint ? ` · ${m.mountPoint}` : ''}</span>
        <Button type="button" size="sm" variant="outline" className={cn(!row.removed && 'ui-button-danger')} onClick={onToggleRemove}>
          {row.removed ? 'Restore' : <><Trash2 className="h-3.5 w-3.5" /> Remove</>}
        </Button>
      </div>

      {row.removed ? (
        <p className="container-modal-empty">
          {isNew
            ? 'Discarded.'
            : `${row.key} will be removed from the config (delete=${row.key}). The underlying storage content is NOT deleted.`}
        </p>
      ) : row.advanced ? (
        <>
          <label className="service-modal-label">
            Raw option string
            <Textarea value={m.raw ?? generated} rows={2} onChange={(e) => onSetRaw(e.target.value)} />
          </label>
          <div className="container-modal-actions">
            <Button type="button" size="sm" variant="outline" onClick={() => onSetAdvanced(false)}>Back to structured</Button>
          </div>
        </>
      ) : (
        <>
          <div className="container-modal-edit-grid">
            <LxcField label="Volume / source" value={m.volume ?? ''} onChange={(v) => onPatch({ volume: v })} placeholder="local-lvm:8 or /host/path" help={isNew ? '(storage:size or bind path)' : undefined} />
            <LxcField label="Mount path" value={m.mountPoint ?? ''} onChange={(v) => onPatch({ mountPoint: v })} placeholder="/data" />
            <LxcField label="Size" value={m.size ?? ''} onChange={(v) => onPatch({ size: v })} placeholder="20G" />
            <LxcField label="Mount options" value={m.mountOptions ?? ''} onChange={(v) => onPatch({ mountOptions: v })} placeholder="noatime" />
          </div>
          <div className="container-modal-actions">
            <LxcCheck label="Read-only" checked={!!m.readOnly} onChange={(c) => onPatch({ readOnly: c })} />
            <LxcCheck label="Backup" checked={!!m.backup} onChange={(c) => onPatch({ backup: c })} />
            <LxcCheck label="Quota" checked={!!m.quota} onChange={(c) => onPatch({ quota: c })} />
            <LxcCheck label="ACL" checked={!!m.acl} onChange={(c) => onPatch({ acl: c })} />
            <LxcCheck label="Shared" checked={!!m.shared} onChange={(c) => onPatch({ shared: c })} />
            <LxcCheck label="Replicate" checked={!!m.replicate} onChange={(c) => onPatch({ replicate: c })} />
          </div>
          {hasUnknownOptions(m.extra) && (
            <p className="container-modal-empty">
              <TriangleAlert className="h-3.5 w-3.5 inline" /> Options Stashboard doesn't model are preserved: <code className="container-modal-code">{m.extra}</code>
            </p>
          )}
          <RawExpander generated={generated} onEditRaw={() => { onSetRaw(generated); onSetAdvanced(true) }} />
        </>
      )}
    </div>
  )
}

function RootfsCard({ model: m, advanced, onPatch, onSetAdvanced, onSetRaw }: {
  model: ProxmoxLxcRootfsChange
  advanced: boolean
  onPatch: (patch: Partial<ProxmoxLxcRootfsChange>) => void
  onSetAdvanced: (adv: boolean) => void
  onSetRaw: (raw: string) => void
}) {
  const generated = formatRootfs(m)
  return (
    <div className="lxc-config-row">
      {advanced ? (
        <>
          <label className="service-modal-label">
            Raw option string
            <Textarea value={m.raw ?? generated} rows={2} onChange={(e) => onSetRaw(e.target.value)} />
          </label>
          <div className="container-modal-actions">
            <Button type="button" size="sm" variant="outline" onClick={() => onSetAdvanced(false)}>Back to structured</Button>
          </div>
        </>
      ) : (
        <>
          <div className="container-modal-edit-grid">
            <label className="service-modal-label">
              Volume <span className="service-modal-label-help">(read-only — migration is out of scope)</span>
              <Input value={m.volume ?? ''} readOnly disabled />
            </label>
            <LxcField label="Size" value={m.size ?? ''} onChange={(v) => onPatch({ size: v })} placeholder="8G" />
            <LxcField label="Mount options" value={m.mountOptions ?? ''} onChange={(v) => onPatch({ mountOptions: v })} placeholder="noatime" />
          </div>
          <div className="container-modal-actions">
            <LxcCheck label="Read-only" checked={!!m.readOnly} onChange={(c) => onPatch({ readOnly: c })} />
            <LxcCheck label="Quota" checked={!!m.quota} onChange={(c) => onPatch({ quota: c })} />
            <LxcCheck label="ACL" checked={!!m.acl} onChange={(c) => onPatch({ acl: c })} />
            <LxcCheck label="Replicate" checked={!!m.replicate} onChange={(c) => onPatch({ replicate: c })} />
          </div>
          {hasUnknownOptions(m.extra) && (
            <p className="container-modal-empty">
              <TriangleAlert className="h-3.5 w-3.5 inline" /> Options Stashboard doesn't model are preserved: <code className="container-modal-code">{m.extra}</code>
            </p>
          )}
          <RawExpander generated={generated} onEditRaw={() => { onSetRaw(generated); onSetAdvanced(true) }} />
        </>
      )}
    </div>
  )
}

function ConfigLineList({ lines }: { lines: ProxmoxLxcDetail['mounts'] }) {
  return (
    <dl className="container-modal-summary">
      {lines.map((l) => (
        <Fragment key={l.key}>
          <dt>{l.key}</dt>
          <dd><code className="container-modal-code container-modal-code-block">{l.value}</code></dd>
        </Fragment>
      ))}
    </dl>
  )
}

// ── Stats tab ───────────────────────────────────────────────────────────────

const TIMEFRAMES: ReadonlyArray<{ id: ProxmoxRrdTimeframe; label: string }> = [
  { id: 'hour', label: 'Hour' },
  { id: 'day', label: 'Day' },
  { id: 'week', label: 'Week' },
]

const CPU_COLOR = '#22c55e'
const MEM_COLOR = '#3b82f6'
const NET_IN_COLOR = '#22c55e'
const NET_OUT_COLOR = '#f59e0b'
const DISK_R_COLOR = '#06b6d4'
const DISK_W_COLOR = '#ef4444'

function StatsTab({ connectionId, vmId, kind }: { connectionId: string; vmId: number; kind: ProxmoxGuestKind }) {
  const [mode, setMode] = useState<'live' | 'history'>('live')
  return (
    <div className="docker-stats-body">
      <div className="container-modal-stats-header">
        <div className="container-modal-actions">
          <Button type="button" size="sm" variant={mode === 'live' ? 'default' : 'outline'} onClick={() => setMode('live')}>Live</Button>
          <Button type="button" size="sm" variant={mode === 'history' ? 'default' : 'outline'} onClick={() => setMode('history')}>History</Button>
        </div>
      </div>
      {mode === 'live'
        ? <LiveStats connectionId={connectionId} vmId={vmId} kind={kind} />
        : <HistoryStats connectionId={connectionId} vmId={vmId} kind={kind} />}
    </div>
  )
}

// Real-time view. Proxmox has no stats stream for LXC, so poll status/current
// every 2 s and keep a rolling window — mirrors the Docker live stats panel.
const LIVE_WINDOW = 120
const LIVE_INTERVAL_MS = 2000

interface LiveSample { t: number; cpu: number; mem: number; maxmem: number; netin: number; netout: number; diskread: number; diskwrite: number }

function LiveStats({ connectionId, vmId, kind }: { connectionId: string; vmId: number; kind: ProxmoxGuestKind }) {
  const [samples, setSamples] = useState<LiveSample[]>([])
  const [error, setError] = useState<string | null>(null)
  const [paused, setPaused] = useState(false)

  useEffect(() => {
    if (paused) return
    let cancelled = false
    const poll = async () => {
      try {
        const s = await fetchProxmoxLxcStatus(connectionId, vmId, kind)
        if (cancelled) return
        setError(null)
        setSamples((prev) => {
          const next = [...prev, {
            t: Date.now(),
            cpu: s.cpu ?? 0,
            mem: s.memUsed ?? 0,
            maxmem: s.memMax ?? 0,
            netin: s.netIn ?? 0,
            netout: s.netOut ?? 0,
            diskread: s.diskRead ?? 0,
            diskwrite: s.diskWrite ?? 0,
          }]
          return next.length > LIVE_WINDOW ? next.slice(next.length - LIVE_WINDOW) : next
        })
      } catch (e) {
        if (!cancelled) setError(getApiErrorMessage(e) ?? 'Failed to read status')
      }
    }
    void poll()
    const id = window.setInterval(() => void poll(), LIVE_INTERVAL_MS)
    return () => { cancelled = true; window.clearInterval(id) }
  }, [connectionId, vmId, kind, paused])

  const cpu = useMemo(() => samples.map((s) => s.cpu * 100), [samples])
  const mem = useMemo(() => samples.map((s) => s.mem), [samples])
  const memMax = samples.length ? samples[samples.length - 1].maxmem : 0
  const { netIn, netOut, diskRead, diskWrite } = useMemo(() => {
    const ni: number[] = []
    const no: number[] = []
    const dr: number[] = []
    const dw: number[] = []
    const rate = (cur: number, prev: number, dt: number) => (dt > 0 ? Math.max(0, (cur - prev) / dt) : 0)
    for (let i = 0; i < samples.length; i++) {
      if (i === 0) { ni.push(0); no.push(0); dr.push(0); dw.push(0); continue }
      const dt = (samples[i].t - samples[i - 1].t) / 1000
      ni.push(rate(samples[i].netin, samples[i - 1].netin, dt))
      no.push(rate(samples[i].netout, samples[i - 1].netout, dt))
      dr.push(rate(samples[i].diskread, samples[i - 1].diskread, dt))
      dw.push(rate(samples[i].diskwrite, samples[i - 1].diskwrite, dt))
    }
    return { netIn: ni, netOut: no, diskRead: dr, diskWrite: dw }
  }, [samples])

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
          <StatTile label="Memory"
            valueText={`${formatBytes(mem.at(-1) ?? 0) ?? '0 B'}${memMax ? ` / ${formatBytes(memMax)}` : ''}`}
            max={memMax || undefined}
            series={[{ data: mem, color: MEM_COLOR, label: 'Used', format: (v) => formatBytes(v) ?? '0 B' }]} />
          <StatTile label="Network ↓ / ↑"
            valueText={`${formatRate(netIn.at(-1))} / ${formatRate(netOut.at(-1))}`}
            series={[
              { data: netIn, color: NET_IN_COLOR, label: 'In', format: formatRate },
              { data: netOut, color: NET_OUT_COLOR, label: 'Out', format: formatRate },
            ]} />
          <StatTile label="Disk I/O R / W"
            valueText={`${formatRate(diskRead.at(-1))} / ${formatRate(diskWrite.at(-1))}`}
            series={[
              { data: diskRead, color: DISK_R_COLOR, label: 'Read', format: formatRate },
              { data: diskWrite, color: DISK_W_COLOR, label: 'Write', format: formatRate },
            ]} />
        </div>
      )}
    </>
  )
}

function HistoryStats({ connectionId, vmId, kind }: { connectionId: string; vmId: number; kind: ProxmoxGuestKind }) {
  const [timeframe, setTimeframe] = useState<ProxmoxRrdTimeframe>('hour')
  const query = useProxmoxLxcRrd(connectionId, vmId, timeframe, true, kind)
  const points = useMemo(() => query.data ?? [], [query.data])

  const series = useMemo(() => ({
    cpu: points.map((p) => (p.cpu ?? 0) * 100),
    mem: points.map((p) => p.memUsed ?? 0),
    netIn: points.map((p) => p.netIn ?? 0),
    netOut: points.map((p) => p.netOut ?? 0),
    diskRead: points.map((p) => p.diskRead ?? 0),
    diskWrite: points.map((p) => p.diskWrite ?? 0),
  }), [points])

  const memMax = points.length ? (points[points.length - 1].memMax ?? 0) : 0

  return (
    <>
      <div className="container-modal-stats-header">
        <div className="container-modal-actions">
          {TIMEFRAMES.map((t) => (
            <Button key={t.id} type="button" size="sm" variant={timeframe === t.id ? 'default' : 'outline'} onClick={() => setTimeframe(t.id)}>
              {t.label}
            </Button>
          ))}
        </div>
        {query.isFetching && <span className="container-modal-empty"><Loader2 className="h-3.5 w-3.5 inline animate-spin" /> updating…</span>}
      </div>

      {query.isLoading && <p className="container-modal-empty">Loading metrics from Proxmox…</p>}
      {query.error && (
        <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read metrics'}</p>
      )}
      {!query.isLoading && !query.error && points.length === 0 && (
        <p className="container-modal-empty">No metrics for this timeframe (the guest may be stopped).</p>
      )}

      {points.length > 0 && (
        <div className="docker-stats-grid">
          <StatTile label="CPU" valueText={`${(series.cpu.at(-1) ?? 0).toFixed(1)}%`} max={100}
            series={[{ data: series.cpu, color: CPU_COLOR, label: 'CPU', format: (v) => `${v.toFixed(1)}%` }]} />
          <StatTile label="Memory"
            valueText={`${formatBytes(series.mem.at(-1) ?? 0) ?? '0 B'}${memMax ? ` / ${formatBytes(memMax)}` : ''}`}
            max={memMax || undefined}
            series={[{ data: series.mem, color: MEM_COLOR, label: 'Used', format: (v) => formatBytes(v) ?? '0 B' }]} />
          <StatTile label="Network ↓ / ↑"
            valueText={`${formatRate(series.netIn.at(-1))} / ${formatRate(series.netOut.at(-1))}`}
            series={[
              { data: series.netIn, color: NET_IN_COLOR, label: 'In', format: formatRate },
              { data: series.netOut, color: NET_OUT_COLOR, label: 'Out', format: formatRate },
            ]} />
          <StatTile label="Disk I/O R / W"
            valueText={`${formatRate(series.diskRead.at(-1))} / ${formatRate(series.diskWrite.at(-1))}`}
            series={[
              { data: series.diskRead, color: DISK_R_COLOR, label: 'Read', format: formatRate },
              { data: series.diskWrite, color: DISK_W_COLOR, label: 'Write', format: formatRate },
            ]} />
        </div>
      )}
    </>
  )
}

// ── Tasks tab ───────────────────────────────────────────────────────────────

function TasksTab({ connectionId, vmId, kind }: { connectionId: string; vmId: number; kind: ProxmoxGuestKind }) {
  const query = useProxmoxLxcTasks(connectionId, vmId, true, kind)
  const [openUpid, setOpenUpid] = useState<string | null>(null)
  const tasks = query.data ?? []

  if (query.isLoading) return <p className="container-modal-empty">Loading tasks from Proxmox…</p>
  if (query.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to read tasks'}
      </p>
    )
  }
  if (tasks.length === 0) return <p className="container-modal-empty">No recent tasks for this container.</p>

  return (
    <div className="container-modal-inspect">
      {tasks.map((t) => (
        <TaskRow
          key={t.upid}
          connectionId={connectionId}
          task={t}
          open={openUpid === t.upid}
          onToggle={(open) => setOpenUpid(open ? t.upid : null)}
        />
      ))}
    </div>
  )
}

function TaskStatusText({ task }: { task: ProxmoxTask }) {
  if (task.status === 'OK') return <span className="text-green-600">OK</span>
  if (!task.status) return <span className="text-yellow-600">running</span>
  return <span className="text-red-600">{task.status}</span>
}

function TaskHighlight({ task }: { task: ProxmoxTask }) {
  if (task.status === 'OK') return 'task-status-up'
  if (!task.status) return 'task-status-attention'
  return 'task-status-down'
}

function TaskRow({ connectionId, task, open, onToggle }: {
  connectionId: string
  task: ProxmoxTask
  open: boolean
  onToggle: (open: boolean) => void
}) {
  const log = useProxmoxTaskLog(connectionId, open ? task.upid : null)
  const running = !task.status
  const ok = task.status === 'OK'
  const color = TaskHighlight({ task })

  return (
    <details
      className="container-modal-collapsible"
      open={open}
      onToggle={(e) => onToggle(e.currentTarget.open)}
    >
      <summary className="container-modal-collapsible-with-action">
        <span className= "task-type-row">
          <span style={{ fontFamily: 'var(--font-mono)' }}>{task.type}</span>
          {ok &&
            <span className="task-status">
              <span className={`task-status-inner ${color}`}>
                {running ? <Loader2 className="h-3 w-3 animate-spin" /> : ok ? <CheckCircle2 className="h-3 w-3" /> : null}
                <TaskStatusText task={task} />
              </span>
            </span>
          }
        </span>
        {!ok && task.status != null && (
          <span className="task-status task-warning">
            <span className={`task-status-inner ${color}`}>
              {running ? <Loader2 className="h-3 w-3 animate-spin" /> : ok ? null : <XCircle className="h-3 w-3" />}
              <TaskStatusText task={task} />
            </span>
          </span>
        )}
        <span className="task-time-row">
          <span className="task-start-time">{formatTaskTime(task.startTime)}</span>
          <span className="task-duration">{formatDuration(task.startTime, task.endTime)}</span>
        </span>
      </summary>
      {open && (
        <>
          {log.isLoading && <p className="container-modal-empty">Loading log…</p>}
          {log.error && <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(log.error) ?? 'Failed to read task log'}</p>}
          {log.data != null && (log.data.trim()
            ? <pre className="container-modal-raw">{log.data}</pre>
            : <p className="container-modal-empty">Empty log.</p>)}
          {task.user && <p className="container-modal-empty">by {task.user}</p>}
        </>
      )}
    </details>
  )
}

// ── Formatters ────────────────────────────────────────────────────────────────

function formatBytes(bytes: number | null): string | null {
  if (bytes == null || bytes <= 0) return null
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
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

function relativeTime(iso: string | null, now: number): string {
  if (!iso) return 'never'
  const mins = Math.round((now - new Date(iso).getTime()) / 60000)
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins} min ago`
  const hrs = Math.round(mins / 60)
  if (hrs < 24) return `${hrs} h ago`
  return `${Math.round(hrs / 24)} d ago`
}

function formatRate(bytesPerSec: number | undefined): string {
  return `${formatBytes(bytesPerSec ?? 0) ?? '0 B'}/s`
}

function formatTaskTime(unixSec: number | null): string {
  if (!unixSec) return '—'
  return new Date(unixSec * 1000).toLocaleString()
}

function formatDuration(startSec: number | null, endSec: number | null): string {
  if (!startSec) return '—'
  if (!endSec) return 'running'
  const s = Math.max(0, endSec - startSec)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  const rem = s % 60
  if (m < 60) return rem ? `${m}m ${rem}s` : `${m}m`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m`
}

