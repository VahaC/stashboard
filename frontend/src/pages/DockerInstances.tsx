import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AlertCircle, Plus, RefreshCw, Settings, TerminalSquare } from 'lucide-react'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  useConnectionWatches,
  useDeleteConnectionWatch,
  useDockerConnections,
  useDockerContainerAction,
  useDockerInstanceContainers,
  useDockerProjectUpdate,
  useFeatures,
} from '@/lib/queries'
import type { DockerConnection, DockerContainerCard, DockerWatch } from '@/lib/types'
import { resolveDockerHostType } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { ContainerModal, type ContainerModalTab } from '@/components/ContainerModal'
import { DockerConnectionForm } from '@/components/DockerWatchSection'
import { ContainerCard } from '@/components/docker/ContainerCard'
import { ProjectUpdateDialog } from '@/components/docker/atoms/ProjectUpdateDialog'
import { HostTerminalDialog } from '@/components/docker/HostTerminalDialog'
import { StorageWidget } from '@/components/docker/StorageWidget'
import '@/styles/docker-instances.css'

interface ModalState {
  connectionId: string
  card: DockerContainerCard
  tab: ContainerModalTab
}

type ConnectionModalState =
  | { mode: 'create' }
  | { mode: 'edit'; connection: DockerConnection }

type StateFilter = 'all' | 'running' | 'stopped'

/**
 * V3.5 — Docker instances page. One section per configured Docker
 * connection; inside each section a card grid of every container on
 * that host with Start / Stop / Restart / Remove actions plus a deep
 * link into the watch modal when one tracks the container.
 *
 * Remove is gated by the server-side `StashboardOptions.AllowContainerRemoval`
 * feature flag — when off the button doesn't render, and the matching
 * DELETE endpoint returns 403 even if the user crafts the request by
 * hand.
 */
export function DockerInstances() {
  const connections = useDockerConnections()
  const features = useFeatures()
  const [searchParams, setSearchParams] = useSearchParams()

  // Search + state filter live at the page level so they apply uniformly
  // across every connection section. Compose project filter is a "dim
  // others" — clicking a project badge keeps the page focused on it
  // until the user clicks Clear.
  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState<StateFilter>('all')
  const [composeFilter, setComposeFilter] = useState<string | null>(null)
  const [modal, setModal] = useState<ModalState | null>(null)
  const [connectionModal, setConnectionModal] = useState<ConnectionModalState | null>(null)
  const [handledDeepLink, setHandledDeepLink] = useState<string | null>(null)

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

    const hasConnection = (connections.data ?? []).some((conn) => conn.id === connectionId)
    if (!hasConnection) {
      setHandledDeepLink(deepLinkKey)
      return
    }

    const normalized = deepLinkContainer.replace(/^\/+/, '').toLowerCase()
    const target = (deepLinkContainers.data ?? []).find((card) => {
      const normalizedName = card.name.replace(/^\/+/, '').toLowerCase()
      return normalizedName === normalized || card.id === deepLinkContainer
    })

    if (!target) {
      setHandledDeepLink(deepLinkKey)
      return
    }

    setModal({ connectionId, card: target, tab: 'overview' })
    setHandledDeepLink(deepLinkKey)

    const next = new URLSearchParams(searchParams)
    next.delete('container')
    setSearchParams(next, { replace: true })
  }, [
    connections.data,
    connections.isLoading,
    deepLinkConnectionId,
    deepLinkContainer,
    deepLinkContainers.data,
    deepLinkContainers.isLoading,
    deepLinkKey,
    handledDeepLink,
    modal,
    searchParams,
    setSearchParams,
  ])

  return (
    <div className="docker-instances-page">
      <div className="docker-instances-header">
        <div className="docker-instances-header-top">
          <h1>Docker instances</h1>
          <Button type="button" size="sm" onClick={() => setConnectionModal({ mode: 'create' })}>
            <Plus className="h-3.5 w-3.5" />
            Add connection
          </Button>
        </div>
        <p className="docker-instances-header-sub">
          Live view of every container across your Docker connections. Click an action to start,
          stop, restart, or remove a container — actions are recorded in the per-watch update
          history so there's one audit trail.
        </p>
      </div>

      <div className="docker-instances-toolbar">
        <Input
          className="docker-instances-toolbar-search"
          placeholder="Search by name or image…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <label className="docker-instances-toolbar-filter">
          <input
            type="radio"
            name="state-filter"
            checked={stateFilter === 'all'}
            onChange={() => setStateFilter('all')}
          />
          all
        </label>
        <label className="docker-instances-toolbar-filter">
          <input
            type="radio"
            name="state-filter"
            checked={stateFilter === 'running'}
            onChange={() => setStateFilter('running')}
          />
          running
        </label>
        <label className="docker-instances-toolbar-filter">
          <input
            type="radio"
            name="state-filter"
            checked={stateFilter === 'stopped'}
            onChange={() => setStateFilter('stopped')}
          />
          stopped
        </label>
        {composeFilter && (
          <Button className="docker-instances-toolbar-compose-filter"
            type="button" 
            variant="ghost" 
            size="sm" 
            onClick={() => setComposeFilter(null)}>
              project: {composeFilter} ✕
          </Button>
        )}
      </div>

      {connections.isLoading && <p className="docker-instances-empty">Loading connections…</p>}
      {connections.error && (
        <p className="docker-instances-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(connections.error) ?? 'Failed to load connections'}
        </p>
      )}
      {!connections.isLoading && (connections.data?.length ?? 0) === 0 && (
        <p className="docker-instances-empty">
          No Docker connections configured yet. Add one from a service's Docker watch panel.
        </p>
      )}

      {(connections.data ?? []).map((conn) => (
        <ConnectionSection
          key={conn.id}
          connection={conn}
          search={search.trim().toLowerCase()}
          stateFilter={stateFilter}
          composeFilter={composeFilter}
          allowRemoval={features.data?.allowContainerRemoval ?? false}
          allowHostShellGlobal={features.data?.allowHostShell ?? false}
          onComposeClick={(project) => setComposeFilter((prev) => prev === project ? null : project)}
          onOpenModal={(card, tab) => setModal({ connectionId: conn.id, card, tab })}
          onEditConnection={() => setConnectionModal({ mode: 'edit', connection: conn })}
        />
      ))}

      {modal && (
        <ContainerModalHost
          state={modal}
          allowRemoval={features.data?.allowContainerRemoval ?? false}
          allowExecGlobal={features.data?.allowContainerExec ?? false}
          connection={(connections.data ?? []).find((c) => c.id === modal.connectionId) ?? null}
          onClose={() => setModal(null)}
          onCardRefresh={(card) => setModal((prev) => prev && prev.card.id === card.id ? { ...prev, card } : prev)}
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

/**
 * Thin shell around `ContainerModal` that owns the per-modal action
 * mutation so the buttons inside the Overview tab can reuse the same
 * `useDockerContainerAction` hook the cards use, scoped to the current
 * connection. Lives at the page level so it survives card re-mounts.
 */
function ContainerModalHost({
  state,
  allowRemoval,
  allowExecGlobal,
  connection,
  onClose,
  onCardRefresh,
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
  // Keep the modal's card in sync with the refetched list so state badges
  // / status strings update after a successful action without forcing the
  // user to close and reopen.
  const containersQuery = useDockerInstanceContainers(state.connectionId)
  useMemo(() => {
    if (!containersQuery.data) return
    const fresh = containersQuery.data.find((c) =>
      c.id === state.card.id || c.name === state.card.name)
    if (fresh && fresh !== state.card) onCardRefresh(fresh)
  }, [containersQuery.data, state.card, onCardRefresh])

  // V3.6 — the Watch tab and Overview tab resolve the tracked container (and
  // its optional service link) themselves from the connection's watch list,
  // so no service context needs to be threaded in from here.
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

interface ConnectionSectionProps {
  connection: DockerConnection
  search: string
  stateFilter: StateFilter
  composeFilter: string | null
  allowRemoval: boolean
  /** V5.7 — global host-terminal master switch, for the connection-level Host terminal button. */
  allowHostShellGlobal: boolean
  onComposeClick: (project: string) => void
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
  onEditConnection: () => void
}

function ConnectionSection({
  connection, search, stateFilter, composeFilter, allowRemoval, allowHostShellGlobal,
  onComposeClick, onOpenModal, onEditConnection,
}: ConnectionSectionProps) {
  const containers = useDockerInstanceContainers(connection.id)
  const watches = useConnectionWatches(connection.id)
  const action = useDockerContainerAction(connection.id)
  const deleteWatch = useDeleteConnectionWatch(connection.id)
  // V5.7 — the host terminal is connection-scoped (a shell on the host), so it
  // opens from the connection header rather than a per-container modal tab.
  // SSH-only by construction, so the button only appears for SSH connections.
  const [hostTerminalOpen, setHostTerminalOpen] = useState(false)
  const isSsh = resolveDockerHostType(connection.hostType) === 'Ssh'
  const [actionErrors, setActionErrors] = useState<Record<string, string>>({})

  // V3.6 — index the connection's tracked containers by name so each card can
  // render its update-status badge without a per-card fetch.
  const watchByContainer = useMemo(() => {
    const map = new Map<string, DockerWatch>()
    for (const w of watches.data ?? []) map.set(w.containerName, w)
    return map
  }, [watches.data])

  const filtered = useMemo(() => {
    const list = containers.data ?? []
    return list.filter((c) => {
      if (search) {
        const hay = `${c.name} ${c.image}`.toLowerCase()
        if (!hay.includes(search)) return false
      }
      if (stateFilter === 'running' && c.state.toLowerCase() !== 'running') return false
      if (stateFilter === 'stopped' && c.state.toLowerCase() === 'running') return false
      if (composeFilter && c.composeProject !== composeFilter) return false
      return true
    })
  }, [containers.data, search, stateFilter, composeFilter])

  const handleAction = async (containerName: string, kind: 'start' | 'stop' | 'restart' | 'remove') => {
    setActionErrors((prev) => {
      const next = { ...prev }
      delete next[containerName]
      return next
    })
    try {
      await action.mutateAsync({ containerName, action: kind })
    } catch (err) {
      if (kind === 'remove') throw err
      const message = getApiErrorMessage(err) ?? `Failed to ${kind} ${containerName}`
      setActionErrors((prev) => ({ ...prev, [containerName]: message }))
    }
  }

  const [collapsedGroups, setCollapsedGroups] = useState<Record<string, boolean>>({})
  const toggleGroup = (groupName: string) => setCollapsedGroups((prev) => ({ ...prev, [groupName]: !prev[groupName] }))

  const groupName = connection.name.replace(/\s+/g, '-').toLowerCase();
  const isCollapsed = collapsedGroups[groupName] ?? false
  return (
    <section key={groupName} className="docker-instances-connection">
      <div className="dashboard-group-button"
        role="button"
        tabIndex={0}
        onClick={() => toggleGroup(groupName)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            toggleGroup(groupName)
          }
        }}
      >
        <div className="dashboard-group-title-container">
          <button
            type="button"
            className="dashboard-group-title"
          >
            {isCollapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
            {connection.name}
          </button>
        </div>
        <div className="docker-instances-connection-meta-group">
          <span className="docker-instances-connection-meta">
            {resolveDockerHostType(connection.hostType).toLowerCase()}
            {connection.hostUrl ? ` · ${connection.hostUrl}` : connection.sshHost ? ` · ${connection.sshHost}${connection.sshPort ? `:${connection.sshPort}` : ''}` : ''}
          </span>
          <span className="docker-instances-connection-meta">
            {containers.isLoading ? '…' : `${filtered.length}/${containers.data?.length ?? 0} containers`}
          </span>
        </div>
        <div className="docker-instances-connection-actions">
          {isSsh && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="docker-instances-connection-edit"
              onClick={(e) => {
                e.stopPropagation()
                setHostTerminalOpen(true)
              }}
              title="Open an interactive shell on the Docker host (SSH)"
            >
              <TerminalSquare className="h-3.5 w-3.5" />
              Terminal
            </Button>
          )}
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="docker-instances-connection-edit"
            onClick={(e) => {
              e.stopPropagation()
              onEditConnection()
            }}
          >
            <Settings className="h-3.5 w-3.5" />
            Edit
          </Button>
        </div>
      </div>
      {hostTerminalOpen && (
        <HostTerminalDialog
          connection={connection}
          allowHostShellGlobal={allowHostShellGlobal}
          onClose={() => setHostTerminalOpen(false)}
        />
      )}
      {!isCollapsed && (
        <StorageWidget
          connectionId={connection.id}
          defaultPruneUnused={connection.pruneUnusedImages}
        />
      )}
      {!isCollapsed && (
        <ProjectGroupedGrid
          connectionId={connection.id}
          containers={containers.data}
          containersError={containers.error}
          filtered={filtered}
          watchByContainer={watchByContainer}
          allowRemoval={allowRemoval}
          actionPending={action.isPending || deleteWatch.isPending}
          actionErrors={actionErrors}
          onComposeClick={onComposeClick}
          onCardAction={async (card, kind) => {
            if (kind === 'remove' && card.state.toLowerCase() === 'not found' && card.watchId) {
              await deleteWatch.mutateAsync(card.watchId)
              return
            }
            await handleAction(card.name, kind)
          }}
          onOpenModal={onOpenModal}
        />
      )}
    </section>
  )
}

// ── V5.4 — group containers by their com.docker.compose.project label ──────

interface ProjectGroup {
  /** `null` for standalone containers (no compose project label). */
  project: string | null
  containers: DockerContainerCard[]
}

interface ProjectGroupedGridProps {
  connectionId: string
  containers: DockerContainerCard[] | undefined
  containersError: unknown
  filtered: DockerContainerCard[]
  watchByContainer: Map<string, DockerWatch>
  allowRemoval: boolean
  actionPending: boolean
  actionErrors: Record<string, string>
  onComposeClick: (project: string) => void
  onCardAction: (card: DockerContainerCard, kind: 'start' | 'stop' | 'restart' | 'remove') => Promise<void>
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
}

function ProjectGroupedGrid({
  connectionId,
  containers,
  containersError,
  filtered,
  watchByContainer,
  allowRemoval,
  actionPending,
  actionErrors,
  onComposeClick,
  onCardAction,
  onOpenModal,
}: ProjectGroupedGridProps) {
  // V5.4 — split filtered containers into compose-project groups while
  // preserving order: the first time we see a project name it claims a slot,
  // then every member of that project lands in the same group. Standalone
  // containers (and single-service compose projects — see below) collect
  // into a trailing `null` group so they still show up.
  //
  // Compose always stamps `com.docker.compose.project` on every container it
  // creates, even when the project is a one-liner with a single service. A
  // group of one would render a "0 of 1 tracked services have updates
  // available" header card with an Update project button that does exactly
  // what the container's own Update now does — pure noise. The fix: a
  // compose group with only one container is rendered as a standalone card
  // (the compose-project badge on the card itself still tells the user the
  // container is compose-managed). The grouping shell only appears once
  // ≥2 services share a project name.
  const groups = useMemo<ProjectGroup[]>(() => {
    const byProject = new Map<string, DockerContainerCard[]>()
    const standalone: DockerContainerCard[] = []
    const order: string[] = []
    for (const card of filtered) {
      const key = card.composeProject?.trim()
      if (!key) {
        standalone.push(card)
        continue
      }
      if (!byProject.has(key)) {
        order.push(key)
        byProject.set(key, [])
      }
      byProject.get(key)!.push(card)
    }
    const result: ProjectGroup[] = []
    for (const project of order) {
      const containers = byProject.get(project)!
      if (containers.length < 2) {
        // Single-service compose project → demote to standalone so it doesn't
        // wear a fake group header. Keeps relative ordering with other
        // standalone cards.
        standalone.push(...containers)
        continue
      }
      result.push({ project, containers })
    }
    if (standalone.length > 0) result.push({ project: null, containers: standalone })
    return result
  }, [filtered])

  if (containersError) {
    return (
      <div className="docker-instances-grid">
        <p className="docker-instances-error">
          <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
          {getApiErrorMessage(containersError) ?? 'Failed to list containers'}
        </p>
      </div>
    )
  }

  if (containers && filtered.length === 0) {
    return (
      <div className="docker-instances-grid">
        <p className="docker-instances-empty">
          {containers.length === 0
            ? 'No containers on this host.'
            : 'No containers match the current filter.'}
        </p>
      </div>
    )
  }

  return (
    <div className="docker-instances-project-list">
      {groups.map((group) =>
        group.project === null ? (
          <div key="__standalone" className="docker-instances-grid">
            {group.containers.map((card) => (
              <ContainerCard
                key={card.id || card.name}
                card={card}
                linkedWatch={watchByContainer.get(card.name) ?? null}
                variant="docker-page"
                allowRemoval={allowRemoval}
                busy={actionPending}
                error={actionErrors[card.name]}
                onAction={(kind) => onCardAction(card, kind)}
                onComposeClick={onComposeClick}
                onOpen={(tab) => onOpenModal(card, tab)}
              />
            ))}
          </div>
        ) : (
          <ProjectSection
            key={group.project}
            connectionId={connectionId}
            project={group.project}
            containers={group.containers}
            watchByContainer={watchByContainer}
            allowRemoval={allowRemoval}
            actionPending={actionPending}
            actionErrors={actionErrors}
            onComposeClick={onComposeClick}
            onCardAction={onCardAction}
            onOpenModal={onOpenModal}
          />
        )
      )}
    </div>
  )
}

interface ProjectSectionProps {
  connectionId: string
  project: string
  containers: DockerContainerCard[]
  watchByContainer: Map<string, DockerWatch>
  allowRemoval: boolean
  actionPending: boolean
  actionErrors: Record<string, string>
  onComposeClick: (project: string) => void
  onCardAction: (card: DockerContainerCard, kind: 'start' | 'stop' | 'restart' | 'remove') => Promise<void>
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
}

/**
 * V5.4 — header card for a single Compose project plus the grid of its
 * services. The header surfaces the "N of M services have updates available"
 * counter and the **Update project** button that drives the bulk-update
 * endpoint. One audit row per service is written server-side under a single
 * aggregate parent.
 */
function ProjectSection({
  connectionId,
  project,
  containers,
  watchByContainer,
  allowRemoval,
  actionPending,
  actionErrors,
  onComposeClick,
  onCardAction,
  onOpenModal,
}: ProjectSectionProps) {
  const projectUpdate = useDockerProjectUpdate(connectionId)
  const [dialogOpen, setDialogOpen] = useState(false)

  // Count "has updates" against the user's tracked watches — an untracked
  // container has no signal we can show without polling the registry.
  const tracked = containers
    .map((c) => watchByContainer.get(c.name))
    .filter((w): w is DockerWatch => Boolean(w))
  const withUpdates = tracked.filter((w) => w.updateStatus === 'UpdateAvailable' || w.updateStatus === 2)

  return (
    <section className="docker-instances-project">
      <div className="docker-instances-project-header">
        <div className="docker-instances-project-header-meta">
          <button
            type="button"
            className="docker-instances-project-badge"
            onClick={() => onComposeClick(project)}
            title="Filter the page to only this project"
          >
            {project}
          </button>
          <span className="docker-instances-project-counter">
            {tracked.length === 0
              ? `${containers.length} service${containers.length === 1 ? '' : 's'} · no tracked watches`
              : `${withUpdates.length} of ${tracked.length} tracked service${tracked.length === 1 ? '' : 's'} have updates available`}
          </span>
        </div>
        <div className="docker-instances-project-header-actions">
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => setDialogOpen(true)}
            disabled={projectUpdate.isPending}
            title="Pull every image in this project and recreate the services in dependency order"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${projectUpdate.isPending ? 'animate-spin' : ''}`} />
            {projectUpdate.isPending ? 'Updating…' : 'Update project'}
          </Button>
        </div>
      </div>
      <div className="docker-instances-grid">
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
            onComposeClick={onComposeClick}
            onOpen={(tab) => onOpenModal(card, tab)}
          />
        ))}
      </div>
      <ProjectUpdateDialog
        open={dialogOpen}
        project={project}
        containers={containers}
        watchByContainer={watchByContainer}
        onConfirm={async () => projectUpdate.mutateAsync(project)}
        onClose={() => setDialogOpen(false)}
      />
    </section>
  )
}
