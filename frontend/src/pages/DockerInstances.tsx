import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQueries } from '@tanstack/react-query'
import {
  AlertCircle,
  ChevronRight,
  Download,
  FileCode,
  FolderPlus,
  Grid,
  MoreVertical,
  Plus,
  RefreshCw,
  Search,
  Settings,
  TerminalSquare,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { FloatingMenu } from '@/components/shared/FloatingMenu'
import { api } from '@/lib/api'
import {
  qk,
  useConnectionWatches,
  useDeleteConnectionWatch,
  useDockerConnections,
  useDockerContainerAction,
  useDockerInstanceContainers,
  useDockerProjectUpdate,
  useFeatures,
} from '@/lib/queries'
import type {
  DockerConnection,
  DockerContainerCard,
  DockerWatch,
} from '@/lib/types'
import { resolveDockerHostType } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { ContainerModal, type ContainerModalTab } from '@/components/ContainerModal'
import { DockerConnectionForm } from '@/components/DockerWatchSection'
import { ContainerCard } from '@/components/docker/ContainerCard'
import { ComposeProjectModal } from '@/components/docker/ComposeProjectModal'
import { ComposeNewProjectModal } from '@/components/docker/ComposeNewProjectModal'
import { ProjectUpdateDialog } from '@/components/docker/atoms/ProjectUpdateDialog'
import { HostTerminalDialog } from '@/components/docker/HostTerminalDialog'
import { StorageWidget } from '@/components/docker/StorageWidget'
import '@/styles/docker-instances.css'

type StateFilter = 'all' | 'running' | 'stopped'

interface ModalState {
  connectionId: string
  card: DockerContainerCard
  tab: ContainerModalTab
}

type ConnectionModalState =
  | { mode: 'create' }
  | { mode: 'edit'; connection: DockerConnection }

// V7.1.1 — the Compose viewer/editor is now a modal scoped to one discovered
// project on one connection, opened from the project group header or a single
// compose-managed container's card.
interface ComposeModalState {
  connectionId: string
  project: string
}

// V7.4.1 — the "New project" dialog, scoped to one connection.
interface NewProjectModalState {
  connectionId: string
  connectionName: string | null
}

// V5.9 — Per-user display preferences for the Docker instances page. We keep
// them in localStorage rather than threading them through the server-side
// dashboard-preferences blob: the choices here (density, diagnostics, storage
// style) are device-local and don't need to follow the user across browsers.
interface DockerPagePrefs {
  density: 'comfortable' | 'compact'
  diagnostics: boolean
  storage: 'collapsed' | 'panel'
}
const PREFS_DEFAULTS: DockerPagePrefs = { density: 'comfortable', diagnostics: true, storage: 'collapsed' }
const PREFS_KEY = 'stashboard.dockerInstances.prefs.v1'

function readPrefs(): DockerPagePrefs {
  try {
    const raw = localStorage.getItem(PREFS_KEY)
    if (!raw) return PREFS_DEFAULTS
    return { ...PREFS_DEFAULTS, ...(JSON.parse(raw) as Partial<DockerPagePrefs>) }
  } catch {
    return PREFS_DEFAULTS
  }
}

function writePrefs(next: DockerPagePrefs) {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(next)) } catch { /* quota / private mode */ }
}

/** Card grid math from the handoff README — single source so summary,
 *  switcher, and groups all agree on column counts. */
interface PackLayout {
  cardW: number
  gap: number
  maxCols: number
}

/** `avail` here is the actual pixel width available to cards (i.e. the
 *  `.dock` element's `clientWidth` MINUS its own horizontal padding). The
 *  caller does the measuring so we don't have to guess at any ancestor's
 *  padding / scrollbar — `ResizeObserver` on the `.dock` element gives us
 *  the truth in one shot. */
function computePack(avail: number, density: DockerPagePrefs['density']): PackLayout {
  const safe = Math.max(0, avail)
  const gap = density === 'compact' ? 10 : 14
  const base = density === 'compact' ? 258 : 300
  let cardW = Math.min(base, safe)
  const maxCols = Math.max(1, Math.floor((safe + gap) / (cardW + gap)))
  if (maxCols === 1) cardW = safe
  return { cardW, gap, maxCols }
}

/** Measure the `.dock` element's inner content width via `ResizeObserver`
 *  and re-pack when it changes. Using the actual element instead of
 *  `window.innerWidth` keeps the math correct regardless of the
 *  `.app-content` parent's padding, its own `overflow: auto` scrollbar, or
 *  any future sidebar / panel that eats horizontal space. */
function usePackLayout(
  density: DockerPagePrefs['density'],
): [PackLayout, React.RefCallback<HTMLDivElement>] {
  const [layout, setLayout] = useState<PackLayout>(() => computePack(1264, density))
  const elRef = useRef<HTMLDivElement | null>(null)
  const obsRef = useRef<ResizeObserver | null>(null)

  // Recompute from the element's current dimensions.
  const measure = useCallback(() => {
    const el = elRef.current
    if (!el) return
    const cs = getComputedStyle(el)
    const inner = el.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight)
    setLayout((prev) => {
      const next = computePack(inner, density)
      if (prev.cardW === next.cardW && prev.gap === next.gap && prev.maxCols === next.maxCols) {
        return prev
      }
      return next
    })
  }, [density])

  // Re-measure whenever density flips (gap / base card change).
  useLayoutEffect(() => { measure() }, [measure])

  // Ref callback that wires up the ResizeObserver. Doing it here (rather
  // than in a useEffect that depends on a ref) means we attach as soon as
  // the .dock element mounts and detach the moment it leaves the tree.
  const setRef = useCallback<React.RefCallback<HTMLDivElement>>((el) => {
    if (obsRef.current) {
      obsRef.current.disconnect()
      obsRef.current = null
    }
    elRef.current = el
    if (!el) return
    measure()
    if (typeof ResizeObserver !== 'undefined') {
      obsRef.current = new ResizeObserver(() => measure())
      obsRef.current.observe(el)
    }
  }, [measure])

  return [layout, setRef]
}

export function DockerInstances() {
  const connections = useDockerConnections()
  const features = useFeatures()
  const [searchParams, setSearchParams] = useSearchParams()

  const [prefs, setPrefs] = useState<DockerPagePrefs>(readPrefs)
  const updatePrefs = useCallback((partial: Partial<DockerPagePrefs>) => {
    setPrefs((prev) => {
      const next = { ...prev, ...partial }
      writePrefs(next)
      return next
    })
  }, [])

  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState<StateFilter>('all')
  const [activeHost, setActiveHost] = useState<string>('all')
  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({})
  const [modal, setModal] = useState<ModalState | null>(null)
  const [connectionModal, setConnectionModal] = useState<ConnectionModalState | null>(null)
  const [composeModal, setComposeModal] = useState<ComposeModalState | null>(null)
  const [newProjectModal, setNewProjectModal] = useState<NewProjectModalState | null>(null)
  const [handledDeepLink, setHandledDeepLink] = useState<string | null>(null)

  const [layout, dockRef] = usePackLayout(prefs.density)

  // Containers + watches for every connection in one place — the per-host
  // sections + summary strip + switcher all read from this single fan-out so
  // they agree (and React-Query de-duplicates with the section's own hook
  // calls because the query keys match).
  const conns = useMemo(() => connections.data ?? [], [connections.data])
  const containerQueries = useQueries({
    queries: conns.map((conn) => ({
      queryKey: qk.dockerInstanceContainers(conn.id),
      queryFn: async (): Promise<DockerContainerCard[]> =>
        (await api.get<DockerContainerCard[]>(
          `/api/docker/connections/${conn.id}/instance/containers`)).data,
      enabled: true,
      refetchInterval: 10_000,
      staleTime: 5_000,
    })),
  })
  const watchQueries = useQueries({
    queries: conns.map((conn) => ({
      queryKey: qk.connectionWatches(conn.id),
      queryFn: async (): Promise<DockerWatch[]> =>
        (await api.get<DockerWatch[]>(`/api/docker/connections/${conn.id}/watches`)).data,
      enabled: true,
      staleTime: 30_000,
    })),
  })
  const containersByHost = useMemo(() => {
    const map = new Map<string, DockerContainerCard[]>()
    conns.forEach((c, i) => map.set(c.id, containerQueries[i]?.data ?? []))
    return map
  }, [conns, containerQueries])
  const updatesByHost = useMemo(() => {
    const map = new Map<string, number>()
    conns.forEach((c, i) => {
      const list = watchQueries[i]?.data ?? []
      map.set(c.id, list.filter((w) => w.updateStatus === 'UpdateAvailable' || w.updateStatus === 2).length)
    })
    return map
  }, [conns, watchQueries])

  // Aggregated totals for the summary strip (every host, ignoring filters).
  const totals = useMemo(() => {
    let total = 0
    let running = 0
    let stopped = 0
    let updates = 0
    for (const list of containersByHost.values()) {
      for (const c of list) {
        total++
        if (c.state.toLowerCase() === 'running') running++
        else stopped++
      }
    }
    for (const n of updatesByHost.values()) updates += n
    return { total, running, stopped, updates, hosts: conns.length }
  }, [containersByHost, updatesByHost, conns])

  const trimmedSearch = search.trim().toLowerCase()

  // ── Deep-link: open container modal when ?connection=…&container=… ───
  const deepLinkConnectionId = searchParams.get('connection')
  const deepLinkContainer = searchParams.get('container')
  const deepLinkKey = deepLinkConnectionId && deepLinkContainer
    ? `${deepLinkConnectionId}::${deepLinkContainer}`
    : null
  const deepLinkContainers = useDockerInstanceContainers(deepLinkConnectionId)

  useEffect(() => {
    if (!deepLinkKey) return
    if (deepLinkKey === handledDeepLink) return
    if (modal) return
    if (connections.isLoading || deepLinkContainers.isLoading) return

    const connectionId = deepLinkConnectionId
    if (!connectionId || !deepLinkContainer) {
      setHandledDeepLink(deepLinkKey)
      return
    }
    const hasConnection = (connections.data ?? []).some((c) => c.id === connectionId)
    if (!hasConnection) {
      setHandledDeepLink(deepLinkKey)
      return
    }
    const normalized = deepLinkContainer.replace(/^\/+/, '').toLowerCase()
    const target = (deepLinkContainers.data ?? []).find((card) => {
      const n = card.name.replace(/^\/+/, '').toLowerCase()
      return n === normalized || card.id === deepLinkContainer
    })
    if (!target) {
      setHandledDeepLink(deepLinkKey)
      return
    }
    setActiveHost(connectionId)
    setModal({ connectionId, card: target, tab: 'overview' })
    setHandledDeepLink(deepLinkKey)
    const next = new URLSearchParams(searchParams)
    next.delete('container')
    setSearchParams(next, { replace: true })
  }, [
    connections.data, connections.isLoading,
    deepLinkConnectionId, deepLinkContainer, deepLinkContainers.data,
    deepLinkContainers.isLoading, deepLinkKey, handledDeepLink, modal,
    searchParams, setSearchParams,
  ])

  const visibleConnections = activeHost === 'all'
    ? conns
    : conns.filter((c) => c.id === activeHost)

  const toggleGroup = useCallback((key: string) => {
    setOpenGroups((prev) => {
      const current = prev[key] !== false // default open
      return { ...prev, [key]: !current }
    })
  }, [])

  return (
    <div
      ref={dockRef}
      className="dock"
      data-density={prefs.density}
      data-diag={prefs.diagnostics ? 'on' : 'off'}
      data-storage={prefs.storage}
    >
      <header className="dock-header">
        <div className="dock-header-text">
          <h1 className="dock-title">Docker instances</h1>
        </div>
        <Button
          type="button"
          size="sm"
          className="dock-header-add"
          onClick={() => setConnectionModal({ mode: 'create' })}
        >
          <Plus className="h-3.5 w-3.5" />
          Add connection
        </Button>
      </header>
      <div className='dock-header'>
        <p className="dock-sub">
          Live view of every container across your Docker connections. Click an action to start,
          stop, restart, or remove a container — actions are recorded in the per-watch update
          history so there's one audit trail.
        </p>
        <PrefsBar prefs={prefs} onChange={updatePrefs} />
      </div>


      <div className="dock-summary">
        <div className="sumtile">
          <div className="sumtile-k"><Grid className="h-3 w-3" />Containers</div>
          <div className="sumtile-v">{totals.total}<small>/ {totals.hosts} host{totals.hosts === 1 ? '' : 's'}</small></div>
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
        connections={conns}
        active={activeHost}
        onChange={setActiveHost}
        containersByHost={containersByHost}
        updatesByHost={updatesByHost}
      />

      <div className="dock-toolbar">
        <div className="searchbox">
          <Search className="ic" aria-hidden />
          <input
            className="input"
            placeholder="Search by name or image…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            aria-label="Search containers"
          />
        </div>
        <div className="segmented" role="group" aria-label="State filter">
          {(['all', 'running', 'stopped'] as const).map((v) => (
            <button
              key={v}
              type="button"
              aria-pressed={stateFilter === v}
              onClick={() => setStateFilter(v)}
            >
              {v[0].toUpperCase() + v.slice(1)}
            </button>
          ))}
        </div>
      </div>

      {connections.isLoading && <p className="dock-empty">Loading connections…</p>}
      {connections.error && (
        <p className="dock-error">
          <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
          {getApiErrorMessage(connections.error) ?? 'Failed to load connections'}
        </p>
      )}
      {!connections.isLoading && conns.length === 0 && (
        <p className="dock-empty">
          No Docker connections configured yet. Click <strong>Add connection</strong> above to get started.
        </p>
      )}

      {visibleConnections.map((conn) => (
        <HostSection
          key={conn.id}
          connection={conn}
          containers={containersByHost.get(conn.id) ?? null}
          containerQuery={containerQueries[conns.findIndex((c) => c.id === conn.id)]}
          search={trimmedSearch}
          stateFilter={stateFilter}
          layout={layout}
          openGroups={openGroups}
          onToggleGroup={toggleGroup}
          allowRemoval={features.data?.allowContainerRemoval ?? false}
          allowHostShellGlobal={features.data?.allowHostShell ?? false}
          storagePref={prefs.storage}
          onOpenModal={(card, tab) => setModal({ connectionId: conn.id, card, tab })}
          onEditConnection={() => setConnectionModal({ mode: 'edit', connection: conn })}
          onOpenCompose={(project) => setComposeModal({ connectionId: conn.id, project })}
          onNewProject={() => setNewProjectModal({ connectionId: conn.id, connectionName: conn.name })}
        />
      ))}

      {modal && (
        <ContainerModalHost
          state={modal}
          allowRemoval={features.data?.allowContainerRemoval ?? false}
          allowExecGlobal={features.data?.allowContainerExec ?? false}
          connection={conns.find((c) => c.id === modal.connectionId) ?? null}
          onClose={() => setModal(null)}
          onCardRefresh={(card) => setModal((prev) => prev && prev.card.id === card.id ? { ...prev, card } : prev)}
        />
      )}

      {composeModal && (
        <ComposeProjectModal
          connectionId={composeModal.connectionId}
          project={composeModal.project}
          onClose={() => setComposeModal(null)}
        />
      )}

      {newProjectModal && (
        <ComposeNewProjectModal
          connectionId={newProjectModal.connectionId}
          connectionName={newProjectModal.connectionName}
          onClose={() => setNewProjectModal(null)}
          onCreated={(project, started) => {
            const connectionId = newProjectModal.connectionId
            setNewProjectModal(null)
            // Once it's up its containers carry the compose labels, so the
            // project is discoverable — open its modal. Not started yet → leave
            // the user on the page (the group appears after they start it).
            if (started) setComposeModal({ connectionId, project })
          }}
        />
      )}

      {connectionModal && (
        <Dialog open onOpenChange={(open) => { if (!open) setConnectionModal(null) }}>
          <DialogContent className="service-modal-content">
            <DialogHeader>
              <DialogTitle>
                {connectionModal.mode === 'create'
                  ? 'Add Docker connection'
                  : `Edit connection: ${connectionModal.connection.name}`}
              </DialogTitle>
            </DialogHeader>

            <DockerConnectionForm
              showTitle={false}
              existing={connectionModal.mode === 'edit' ? connectionModal.connection : null}
              onSaved={() => setConnectionModal(null)}
              onCancel={() => setConnectionModal(null)}
            />
          </DialogContent>
        </Dialog>
      )}
    </div>
  )
}

// ── Prefs bar (density / diagnostics / storage) ─────────────────────────

interface PrefsBarProps {
  prefs: DockerPagePrefs
  onChange: (partial: Partial<DockerPagePrefs>) => void
}

function PrefsBar({ prefs, onChange }: PrefsBarProps) {
  return (
    <div className="dock-prefs" aria-label="Display preferences">
      <PrefSegmented
        label="Density"
        value={prefs.density}
        options={[{ v: 'comfortable', l: 'Comfortable' }, { v: 'compact', l: 'Compact' }]}
        onChange={(v) => onChange({ density: v as DockerPagePrefs['density'] })}
      />
      <PrefSegmented
        label="Storage"
        value={prefs.storage}
        options={[{ v: 'collapsed', l: 'Collapsed' }, { v: 'panel', l: 'Panel' }]}
        onChange={(v) => onChange({ storage: v as DockerPagePrefs['storage'] })}
      />
      <label className="dock-prefs-check">
        <input
          type="checkbox"
          checked={prefs.diagnostics}
          onChange={(e) => onChange({ diagnostics: e.target.checked })}
        />
        Diagnostics
      </label>
    </div>
  )
}

interface PrefSegmentedProps {
  label: string
  value: string
  options: Array<{ v: string; l: string }>
  onChange: (v: string) => void
}

function PrefSegmented({ label, value, options, onChange }: PrefSegmentedProps) {
  return (
    <div className="dock-prefs-group">
      <span className="dock-prefs-label">{label}</span>
      <div className="segmented" role="group" aria-label={label}>
        {options.map((o) => (
          <button
            key={o.v}
            type="button"
            aria-pressed={value === o.v}
            onClick={() => onChange(o.v)}
          >
            {o.l}
          </button>
        ))}
      </div>
    </div>
  )
}

// ── Per-host summary card + storage + project groups ────────────────────

interface HostSectionProps {
  connection: DockerConnection
  containers: DockerContainerCard[] | null
  containerQuery: { isLoading?: boolean; error?: unknown } | undefined
  search: string
  stateFilter: StateFilter
  layout: PackLayout
  openGroups: Record<string, boolean>
  onToggleGroup: (key: string) => void
  allowRemoval: boolean
  allowHostShellGlobal: boolean
  storagePref: DockerPagePrefs['storage']
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
  onEditConnection: () => void
  onOpenCompose: (project: string) => void
  onNewProject: () => void
}

function HostSection({
  connection, containers, containerQuery, search, stateFilter, layout, openGroups,
  onToggleGroup, allowRemoval, allowHostShellGlobal, storagePref, onOpenModal, onEditConnection,
  onNewProject,
  onOpenCompose,
}: HostSectionProps) {
  const watches = useConnectionWatches(connection.id)
  const action = useDockerContainerAction(connection.id)
  const deleteWatch = useDeleteConnectionWatch(connection.id)
  const [hostTerminalOpen, setHostTerminalOpen] = useState(false)
  const [actionErrors, setActionErrors] = useState<Record<string, string>>({})
  const [hostMenuPos, setHostMenuPos] = useState<{ x: number; y: number } | null>(null)
  const isSsh = resolveDockerHostType(connection.hostType) === 'Ssh'

  const watchByContainer = useMemo(() => {
    const map = new Map<string, DockerWatch>()
    for (const w of watches.data ?? []) map.set(w.containerName, w)
    return map
  }, [watches.data])

  const filtered = useMemo(() => {
    const list = containers ?? []
    return list.filter((c) => {
      if (search) {
        const hay = `${c.name} ${c.image}`.toLowerCase()
        if (!hay.includes(search)) return false
      }
      if (stateFilter === 'running' && c.state.toLowerCase() !== 'running') return false
      if (stateFilter === 'stopped' && c.state.toLowerCase() === 'running') return false
      return true
    })
  }, [containers, search, stateFilter])

  const handleAction = async (containerName: string, kind: 'start' | 'stop' | 'restart' | 'remove') => {
    setActionErrors((prev) => {
      const next = { ...prev }; delete next[containerName]; return next
    })
    try {
      await action.mutateAsync({ containerName, action: kind })
    } catch (err) {
      if (kind === 'remove') throw err
      const message = getApiErrorMessage(err) ?? `Failed to ${kind} ${containerName}`
      setActionErrors((prev) => ({ ...prev, [containerName]: message }))
    }
  }

  const onCardAction = async (card: DockerContainerCard, kind: 'start' | 'stop' | 'restart' | 'remove') => {
    if (kind === 'remove' && card.state.toLowerCase() === 'not found' && card.watchId) {
      await deleteWatch.mutateAsync(card.watchId)
      return
    }
    await handleAction(card.name, kind)
  }

  const transport = resolveDockerHostType(connection.hostType)
  const endpoint = connection.hostUrl
    ? connection.hostUrl
    : connection.sshHost
      ? `${connection.sshHost}${connection.sshPort ? `:${connection.sshPort}` : ''}`
      : ''
  const total = containers?.length ?? 0
  const online = !containerQuery?.error // best-effort signal — green dot if the list loaded
  const containerError = containerQuery?.error

  return (
    <section className="host-section">
      <div className="host-card" onContextMenu={(e) => { e.preventDefault(); setHostMenuPos({ x: e.clientX, y: e.clientY }) }}>
        <div className="host-card-top">
          <span className="host-name">
            <span className="host-dot" data-off={!online} />
            {connection.name}
          </span>
          <span className="host-endpoint">
            <span className="tp">{transport.toLowerCase()}</span>
            {endpoint && <span>{endpoint}</span>}
            <span className="host-count">· {total} container{total === 1 ? '' : 's'}</span>
          </span>
          <div className="host-actions">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onMouseDown={(e) => e.stopPropagation()}
              onClick={(e) => { const r = e.currentTarget.getBoundingClientRect(); setHostMenuPos(p => p ? null : { x: r.left, y: r.bottom + 4 }) }}
              title="Host actions"
              className="cgroup-menu-trigger"
            >
              <MoreVertical className="h-3.5 w-3.5" />
            </Button>
            {hostMenuPos && (
              <FloatingMenu pos={hostMenuPos} onClose={() => setHostMenuPos(null)}>
                {resolveDockerHostType(connection.hostType) !== 'TcpTls' && (
                  <>
                    <button
                      className="cgroup-menu-item"
                      onClick={() => { setHostMenuPos(null); onNewProject() }}
                    >
                      <FolderPlus className="h-3.5 w-3.5" />
                      New project
                    </button>
                    <div className="cgroup-menu-sep" />
                  </>
                )}
                {isSsh && (
                  <>
                    <button
                      className="cgroup-menu-item"
                      onClick={() => { setHostMenuPos(null); setHostTerminalOpen(true) }}
                    >
                      <TerminalSquare className="h-3.5 w-3.5" />
                      Terminal
                    </button>
                    <div className="cgroup-menu-sep" />
                  </>
                )}
                <button
                  className="cgroup-menu-item"
                  onClick={() => { setHostMenuPos(null); onEditConnection() }}
                >
                  <Settings className="h-3.5 w-3.5" />
                  Edit
                </button>
              </FloatingMenu>
            )}
          </div>
        </div>

        <StorageWidget
          connectionId={connection.id}
          defaultPruneUnused={connection.pruneUnusedImages}
          variant={storagePref}
        />
      </div>

      {hostTerminalOpen && (
        <HostTerminalDialog
          connection={connection}
          allowHostShellGlobal={allowHostShellGlobal}
          onClose={() => setHostTerminalOpen(false)}
        />
      )}

      <ProjectGroups
        connectionId={connection.id}
        isComposable={resolveDockerHostType(connection.hostType) !== 'TcpTls'}
        containers={containers}
        containersError={containerError}
        filtered={filtered}
        watchByContainer={watchByContainer}
        allowRemoval={allowRemoval}
        actionPending={action.isPending || deleteWatch.isPending}
        actionErrors={actionErrors}
        layout={layout}
        openGroups={openGroups}
        onToggleGroup={onToggleGroup}
        onCardAction={onCardAction}
        onOpenModal={onOpenModal}
        onOpenCompose={onOpenCompose}
      />
    </section>
  )
}

// ── Project groups ──────────────────────────────────────────────────────

interface ProjectGroupsProps {
  connectionId: string
  isComposable: boolean
  containers: DockerContainerCard[] | null
  containersError: unknown
  filtered: DockerContainerCard[]
  watchByContainer: Map<string, DockerWatch>
  allowRemoval: boolean
  actionPending: boolean
  actionErrors: Record<string, string>
  layout: PackLayout
  openGroups: Record<string, boolean>
  onToggleGroup: (key: string) => void
  onCardAction: (card: DockerContainerCard, kind: 'start' | 'stop' | 'restart' | 'remove') => Promise<void>
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
  onOpenCompose: (project: string) => void
}

interface ProjectGroup {
  /** `null` for the trailing "Other containers" bucket (no compose project). */
  project: string | null
  containers: DockerContainerCard[]
}

function ProjectGroups({
  connectionId, isComposable, containers, containersError, filtered, watchByContainer, allowRemoval,
  actionPending, actionErrors, layout, openGroups, onToggleGroup, onCardAction, onOpenModal, onOpenCompose,
}: ProjectGroupsProps) {
  // V7.1.1 — every Compose project gets its own group, even a single-container
  // one. The v5.4 rule used to demote 1-of-1 projects into "Other" because a
  // lone-container header looked meaningless — but a single-container project
  // still carries a useful Compose / Update project action, so hiding it under
  // "no compose project" was misleading. Only containers with no compose label
  // land in the trailing "Other containers" bucket now.
  const groups = useMemo<ProjectGroup[]>(() => {
    const byProject = new Map<string, DockerContainerCard[]>()
    const other: DockerContainerCard[] = []
    const order: string[] = []
    for (const card of filtered) {
      const key = card.composeProject?.trim()
      if (!key) { other.push(card); continue }
      if (!byProject.has(key)) { order.push(key); byProject.set(key, []) }
      byProject.get(key)!.push(card)
    }
    const result: ProjectGroup[] = []
    for (const project of order) {
      result.push({ project, containers: byProject.get(project)! })
    }
    if (other.length > 0) result.push({ project: null, containers: other })
    return result
  }, [filtered])

  if (containersError) {
    return (
      <p className="dock-error">
        <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
        {getApiErrorMessage(containersError) ?? 'Failed to list containers'}
      </p>
    )
  }

  if (containers && filtered.length === 0) {
    return (
      <p className="dock-empty">
        {containers.length === 0
          ? 'No containers on this host.'
          : 'No containers match the current filter.'}
      </p>
    )
  }

  return (
    <div className="cgroups">
      {groups.map((g) => (
        <PackedGroup
          key={g.project ?? '__other'}
          groupKey={`${connectionId}/${g.project ?? '__other'}`}
          isOther={g.project === null}
          project={g.project}
          connectionId={connectionId}
          isComposable={isComposable}
          containers={g.containers}
          watchByContainer={watchByContainer}
          allowRemoval={allowRemoval}
          actionPending={actionPending}
          actionErrors={actionErrors}
          layout={layout}
          open={openGroups[`${connectionId}/${g.project ?? '__other'}`] !== false}
          onToggle={onToggleGroup}
          onCardAction={onCardAction}
          onOpenModal={onOpenModal}
          onOpenCompose={onOpenCompose}
        />
      ))}
    </div>
  )
}

interface PackedGroupProps {
  groupKey: string
  isOther: boolean
  project: string | null
  connectionId: string
  isComposable: boolean
  containers: DockerContainerCard[]
  watchByContainer: Map<string, DockerWatch>
  allowRemoval: boolean
  actionPending: boolean
  actionErrors: Record<string, string>
  layout: PackLayout
  open: boolean
  onToggle: (key: string) => void
  onCardAction: (card: DockerContainerCard, kind: 'start' | 'stop' | 'restart' | 'remove') => Promise<void>
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
  onOpenCompose: (project: string) => void
}

function PackedGroup({
  groupKey, isOther, project, connectionId, isComposable, containers, watchByContainer, allowRemoval,
  actionPending, actionErrors, layout, open, onToggle, onCardAction, onOpenModal, onOpenCompose,
}: PackedGroupProps) {
  const projectUpdate = useDockerProjectUpdate(connectionId)
  const [updateDialogOpen, setUpdateDialogOpen] = useState(false)
  const [menuPos, setMenuPos] = useState<{ x: number; y: number } | null>(null)

  const cols = Math.min(containers.length, layout.maxCols)
  const groupWidth = cols * layout.cardW + (cols - 1) * layout.gap
  const gridStyle: React.CSSProperties = {
    gridTemplateColumns: `repeat(${cols}, ${layout.cardW}px)`,
    gap: `${layout.gap}px`,
  }

  // Tracked + with-updates counts drive the "N of M" meta line.
  const tracked = containers
    .map((c) => watchByContainer.get(c.name))
    .filter((w): w is DockerWatch => Boolean(w))
  const withUpdates = tracked.filter((w) => w.updateStatus === 'UpdateAvailable' || w.updateStatus === 2)

  const headMeta: React.ReactNode = isOther
    ? <span className="cgroup-meta">no compose project</span>
    : tracked.length === 0
      ? <span className="cgroup-meta">no tracked watches</span>
      : withUpdates.length > 0
        ? <span className="cgroup-meta"><span className="updates">{withUpdates.length} update{withUpdates.length === 1 ? '' : 's'}</span></span>
        : <span className="cgroup-meta">up to date</span>

  return (
    <div
      className={`cgroup ${open ? 'open' : ''}`}
      style={open ? { width: `${groupWidth}px` } : undefined}
    >
      <div
        className="cgroup-head"
        role="button"
        tabIndex={0}
        onClick={() => onToggle(groupKey)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            onToggle(groupKey)
          }
        }}
      >
        <ChevronRight className="chev h-3.5 w-3.5" />
        <span className={`cgroup-name${isOther ? ' is-other' : ''}`}>
          {isOther ? 'Other containers' : project}
        </span>
        <span className="cgroup-count">{containers.length}</span>
        {headMeta}
        {!isOther && (
          <span className="cgroup-acts">
            <Button
              type="button"
              size="sm"
              variant="outline"
              onMouseDown={(e) => e.stopPropagation()}
              onClick={(e) => { e.stopPropagation(); const r = e.currentTarget.getBoundingClientRect(); setMenuPos(p => p ? null : { x: r.left, y: r.bottom + 4 }) }}
              title="Project actions"
              className="cgroup-menu-trigger"
            >
              <MoreVertical className="h-3.5 w-3.5" />
            </Button>
            {menuPos && (
              <FloatingMenu pos={menuPos} onClose={() => setMenuPos(null)}>
                {isComposable && (
                  <>
                    <button
                      className="cgroup-menu-item"
                      onClick={() => { setMenuPos(null); onOpenCompose(project!) }}
                    >
                      <FileCode className="h-3.5 w-3.5" />
                      Compose
                    </button>
                    <div className="cgroup-menu-sep" />
                  </>
                )}
                <button
                  className="cgroup-menu-item"
                  disabled={projectUpdate.isPending}
                  onClick={() => { setMenuPos(null); setUpdateDialogOpen(true) }}
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${projectUpdate.isPending ? 'animate-spin' : ''}`} />
                  {projectUpdate.isPending ? 'Updating…' : 'Update project'}
                </button>
              </FloatingMenu>
            )}
          </span>
        )}
      </div>
      {open && (
        <div className="cgroup-body">
          <div className="cgroup-grid" style={gridStyle}>
            {containers.map((card) => (
              <ContainerCard
                key={card.id || card.name}
                card={card}
                linkedWatch={watchByContainer.get(card.name) ?? null}
                variant="docker-page"
                allowRemoval={allowRemoval}
                busy={actionPending || projectUpdate.isPending}
                error={actionErrors[card.name]}
                onAction={(kind) => onCardAction(card, kind)}
                onOpen={(tab) => onOpenModal(card, tab)}
              />
            ))}
          </div>
        </div>
      )}
      {!isOther && project && (
        <ProjectUpdateDialog
          open={updateDialogOpen}
          project={project}
          containers={containers}
          watchByContainer={watchByContainer}
          onConfirm={async () => projectUpdate.mutateAsync(project)}
          onClose={() => setUpdateDialogOpen(false)}
        />
      )}
    </div>
  )
}

// ── Connection switcher ─────────────────────────────────────────────────

interface ConnectionSwitcherProps {
  connections: DockerConnection[]
  active: string
  onChange: (next: string) => void
  containersByHost: Map<string, DockerContainerCard[]>
  updatesByHost: Map<string, number>
}

function ConnectionSwitcher({
  connections, active, onChange, containersByHost, updatesByHost,
}: ConnectionSwitcherProps) {
  if (connections.length === 0) return null
  return (
    <div className="switcher" role="tablist" aria-label="Connection">
      <button
        type="button"
        className="conn"
        aria-pressed={active === 'all'}
        onClick={() => onChange('all')}
      >
        <span className="conn-name">All connections</span>
        <span className="conn-count">{connections.length} host{connections.length === 1 ? '' : 's'}</span>
      </button>
      {connections.map((conn) => {
        const list = containersByHost.get(conn.id) ?? []
        const total = list.length
        const running = list.filter((c) => c.state.toLowerCase() === 'running').length
        const updates = updatesByHost.get(conn.id) ?? 0
        return (
          <button
            key={conn.id}
            type="button"
            className="conn"
            aria-pressed={active === conn.id}
            onClick={() => onChange(conn.id)}
            title={conn.name}
          >
            <span className="conn-dot" />
            <span className="conn-name">{conn.name}</span>
            <span className="conn-count">{running}/{total}</span>
            {updates > 0 && <span className="conn-updates">{updates}</span>}
          </button>
        )
      })}
    </div>
  )
}

// ── Container modal host (unchanged from V5.8) ──────────────────────────

function ContainerModalHost({
  state, allowRemoval, allowExecGlobal, connection, onClose, onCardRefresh,
}: {
  state: ModalState
  allowRemoval: boolean
  allowExecGlobal: boolean
  connection: DockerConnection | null
  onClose: () => void
  onCardRefresh: (card: DockerContainerCard) => void
}) {
  const action = useDockerContainerAction(state.connectionId)
  const [error, setError] = useState<string | undefined>(undefined)
  const containersQuery = useDockerInstanceContainers(state.connectionId)
  useMemo(() => {
    if (!containersQuery.data) return
    const fresh = containersQuery.data.find((c) => c.id === state.card.id || c.name === state.card.name)
    if (fresh && fresh !== state.card) onCardRefresh(fresh)
  }, [containersQuery.data, state.card, onCardRefresh])

  const handleAction = async (kind: 'start' | 'stop' | 'restart' | 'remove') => {
    setError(undefined)
    try {
      await action.mutateAsync({ containerName: state.card.name, action: kind })
      if (kind === 'remove') onClose()
    } catch (err) {
      if (kind === 'remove') throw err
      setError(getApiErrorMessage(err) ?? `Failed to ${kind} ${state.card.name}`)
    }
  }

  return (
    <ContainerModal
      connectionId={state.connectionId}
      card={state.card}
      initialTab={state.tab}
      allowRemoval={allowRemoval}
      busy={action.isPending}
      actionError={error}
      allowExec={connection?.allowExec ?? false}
      allowExecGlobal={allowExecGlobal}
      onAction={handleAction}
      onClose={onClose}
    />
  )
}
