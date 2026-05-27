import { useState } from 'react'
import { Activity, AlertCircle, Bell, FileText, MoreHorizontal, Search, Trash2, SquareTerminal } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { DockerContainerCard, DockerWatch } from '@/lib/types'
import { ContainerStateBadge } from './atoms/ContainerStateBadge'
import { UpdateStatusBadge } from './atoms/UpdateStatusBadge'
import { ContainerLifecycleActions } from './atoms/ContainerLifecycleActions'
import { RemoveConfirmDialog } from './atoms/RemoveConfirmDialog'
import type { ContainerModalTab } from '@/components/ContainerModal'

export type ContainerCardVariant = 'docker-page' | 'service-modal'

export type ContainerCardProps = {
  card: DockerContainerCard
  /** Compact link to a Stashboard watch if one tracks this container. */
  linkedWatch?: DockerWatch | null
  /** Decides default modal tab and action density. */
  variant: ContainerCardVariant
  /** Feature flag from /api/features. */
  allowRemoval: boolean
  /** When true, the lifecycle/diagnostics action rows are disabled. */
  busy: boolean
  /** Optional inline error from the last action attempt. */
  error?: string
  /** Caller decides what to do when a tab button is clicked. The
   *  variant decides what tab the bare-body click maps to. */
  onOpen: (tab: ContainerModalTab) => void
  onAction: (action: 'start' | 'stop' | 'restart' | 'remove') => void | Promise<void>
  onComposeClick?: (project: string) => void
}

/**
 * Shared container card. Renders the same shape on `/docker` and inside
 * `ServiceModal → Docker`, with a single compact action row — diagnostics on
 * the left, lifecycle + overflow on the right. Layout matches mockup 5a of
 * the design doc.
 *
 * Click semantics:
 * - Card body click → `onOpen('overview')` (docker-page) or
 *   `onOpen('watch')` (service-modal).
 * - Logs / Stats → `onOpen(tab)` with propagation stopped.
 * - Inspect / Remove live behind the `⋯` overflow menu.
 */
export function ContainerCard({
  card, linkedWatch = null, variant, allowRemoval, busy, error, onOpen, onAction, onComposeClick,
}: ContainerCardProps) {
  const stateLower = card.state.toLowerCase()
  const running = stateLower === 'running'
  const stopped = stateLower === 'exited' || stateLower === 'dead'
  const notFound = stateLower === 'not found'
  const defaultTab: ContainerModalTab = notFound ? 'watch' : variant === 'service-modal' ? 'watch' : 'overview'
  const [removeDialogOpen, setRemoveDialogOpen] = useState(false)

  const requestRemove = () => setRemoveDialogOpen(true)
  const confirmRemove = async () => {
    await (onAction('remove') as Promise<void>)
    setRemoveDialogOpen(false)
  }

  return (
    <div className="cc docker-instances-card" data-container-state={card.state.toLowerCase()}>
      <div
        className="cc-body docker-instances-card-body"
        role="button"
        tabIndex={0}
        onClick={() => onOpen(defaultTab)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            onOpen(defaultTab)
          }
        }}
      >
        <div className="cc-head docker-instances-card-head">
          <span className="cc-name docker-instances-card-name">
            {card.name}
            {variant === 'service-modal' && linkedWatch && (
              <span className="cc-watch-chip">{linkedWatch.label}</span>
            )}
          </span>
          <div className="cc-head-right">
            {linkedWatch && (
              <UpdateStatusBadge
                status={linkedWatch.updateStatus}
                title={`Tracked${linkedWatch.label ? ` as "${linkedWatch.label}"` : ''}`}
                className="cc-update-badge"
              />
            )}
            <ContainerStateBadge state={card.state} />
          </div>
        </div>

        <div className="cc-image docker-instances-card-image">{card.image}</div>

        <div className="cc-meta docker-instances-card-meta">
          Status: {card.status && <span>{card.status}</span>}
        </div>
        <div className="cc-meta docker-instances-card-meta docker-instances-card-ports">
          {card.ports.length > 0 ? card.ports.length === 1 ? `Port: ` : `Ports: ` : ''}
          {card.ports.map((p, idx) => (
            <span key={idx} className="cc-chip docker-instances-card-port">
              {p.publicPort ? `${p.publicPort}→` : ''}{p.privatePort}/{p.type}
            </span>
          ))}
        </div>
      </div>

      <div className="cc-meta docker-instances-card-meta compose-service-row">
        {card.composeProject && (
          <button
            type="button"
            className="cc-chip-link docker-instances-card-watch-link"
            onClick={(e) => {
              e.stopPropagation()
              onComposeClick?.(card.composeProject!)
            }}
            title="Filter to this compose service"
          >
            service: {card.composeProject}
            {card.composeService && ` / ${card.composeService}`}
          </button>
        )}
      </div>
      <div
        // className="cc-actions cc-actions-split docker-instances-card-actions"
        className="cc-actions docker-instances-card-actions"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="cc-actions-secondary">
          {!notFound && (
            <>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpen('inspect')}
                title="Inspect container"
              >
                <Search className="h-3.5 w-3.5" />
                {/* <span className="label-text">Inspect</span> */}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpen('logs')}
                title="Live container logs"
              >
                <FileText className="h-3.5 w-3.5" />
                {/* <span className="label-text">Logs</span> */}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpen('stats')}
                title="Live container stats"
              >
                <Activity className="h-3.5 w-3.5" />
                {/* <span className="label-text">Stats</span> */}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpen('watch')}
                title={linkedWatch ? 'Edit update tracking' : 'Track this container for updates'}
              >
                <Bell className="h-3.5 w-3.5" />
                {/* <span className="label-text">{linkedWatch ? 'Watch' : 'Track'}</span> */}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => onOpen('terminal')}
                title="Open terminal for this container"
              >
                <SquareTerminal className="h-3.5 w-3.5" />
                {/* <span className="label-text">{linkedWatch ? 'Watch' : 'Track'}</span> */}
              </Button>
            </>
          )}
        </div>
        <div className="cc-actions-right">
          {!notFound && (
            <ContainerLifecycleActions
              containerName={card.name}
              running={running}
              allowRemoval={false}
              busy={busy}
              density="full"
              onAction={onAction}
            />
          )}
          {stopped || notFound ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="cc-remove-inline"
              disabled={busy}
              onClick={requestRemove}
            >
              <Trash2 className="h-3.5 w-3.5" />
              <span className="label-text">Remove</span>
            </Button>
          ) : (
            <CardOverflow
              allowRemoval={allowRemoval}
              busy={busy}
              containerName={card.name}
              onRequestRemove={requestRemove}
            />
          )}
        </div>
      </div>

      {error && (
        <p className="cc-action-error docker-instances-card-action-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}

      <RemoveConfirmDialog
        open={removeDialogOpen}
        containerName={card.name}
        image={card.image}
        status={card.status || card.state}
        ghost={notFound}
        onConfirm={confirmRemove}
        onCancel={() => setRemoveDialogOpen(false)}
      />
    </div>
  )
}

interface CardOverflowProps {
  allowRemoval: boolean
  busy: boolean
  containerName: string
  onRequestRemove: () => void
}

function CardOverflow({
  allowRemoval, busy, containerName: _containerName, onRequestRemove,
}: CardOverflowProps) {
  if (!allowRemoval) return null

  return (
    <details className="cc-overflow">
      <summary aria-label="More actions">
        <MoreHorizontal className="h-3.5 w-3.5" />
      </summary>
      <div className="cc-overflow-menu">
        <button
          type="button"
          className="cc-overflow-item cc-overflow-item-danger"
          disabled={busy}
          onClick={onRequestRemove}
        >
          <Trash2 className="h-3.5 w-3.5" /> Remove
        </button>
      </div>
    </details>
  )
}
