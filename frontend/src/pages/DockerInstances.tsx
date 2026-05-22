import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { AlertCircle, Plus, Settings } from 'lucide-react'
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
  useFeatures,
} from '@/lib/queries'
import type { DockerConnection, DockerContainerCard, DockerWatch } from '@/lib/types'
import { resolveDockerHostType } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { ContainerModal, type ContainerModalTab } from '@/components/ContainerModal'
import { DockerConnectionForm } from '@/components/DockerWatchSection'
import { ContainerCard } from '@/components/docker/ContainerCard'
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
          onComposeClick={(project) => setComposeFilter((prev) => prev === project ? null : project)}
          onOpenModal={(card, tab) => setModal({ connectionId: conn.id, card, tab })}
          onEditConnection={() => setConnectionModal({ mode: 'edit', connection: conn })}
        />
      ))}

      {modal && (
        <ContainerModalHost
          state={modal}
          allowRemoval={features.data?.allowContainerRemoval ?? false}
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
  onClose,
  onCardRefresh,
}: {
  state: ModalState
  allowRemoval: boolean
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
  onComposeClick: (project: string) => void
  onOpenModal: (card: DockerContainerCard, tab: ContainerModalTab) => void
  onEditConnection: () => void
}

function ConnectionSection({
  connection, search, stateFilter, composeFilter, allowRemoval, onComposeClick, onOpenModal, onEditConnection,
}: ConnectionSectionProps) {
  const containers = useDockerInstanceContainers(connection.id)
  const watches = useConnectionWatches(connection.id)
  const action = useDockerContainerAction(connection.id)
  const deleteWatch = useDeleteConnectionWatch(connection.id)
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
        <button
          type="button"
          className="dashboard-group-title"
        >
          {isCollapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          {connection.name}
        </button>
        <span className="docker-instances-connection-meta">
          {resolveDockerHostType(connection.hostType).toLowerCase()}
          {connection.hostUrl ? ` · ${connection.hostUrl}` : ''}
        </span>
        <span className="docker-instances-connection-meta">
          {containers.isLoading ? '…' : `${filtered.length}/${containers.data?.length ?? 0} containers`}
        </span>
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
          Edit connection
        </Button>
      </div>
      {!isCollapsed && (
        <div className="docker-instances-grid">
          {containers.error && (
            <p className="docker-instances-error">
              <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(containers.error) ?? 'Failed to list containers'}
            </p>
          )}

          {containers.data && filtered.length === 0 && (
            <p className="docker-instances-empty">
              {containers.data.length === 0
                ? 'No containers on this host.'
                : 'No containers match the current filter.'}
            </p>
          )}

          {filtered.map((card) => (
            <ContainerCard
              key={card.id || card.name}
              card={card}
              linkedWatch={watchByContainer.get(card.name) ?? null}
              variant="docker-page"
              allowRemoval={allowRemoval}
              busy={action.isPending || deleteWatch.isPending}
              error={actionErrors[card.name]}
              onAction={async (kind) => {
                if (kind === 'remove' && card.state.toLowerCase() === 'not found' && card.watchId) {
                  await deleteWatch.mutateAsync(card.watchId)
                  return
                }
                await handleAction(card.name, kind)
              }}
              onComposeClick={onComposeClick}
              onOpen={(tab) => onOpenModal(card, tab)}
            />
          ))}
        </div>
      )}
    </section>
  )
}
