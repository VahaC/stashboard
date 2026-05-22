import type { DockerContainerCard } from '@/lib/types'
import { ContainerStateBadge } from './ContainerStateBadge'

export type ContainerSummaryDLProps = {
  card: DockerContainerCard
  /** When true, drops Created and Compose rows for the tighter Inspect-tab view. */
  compact?: boolean
}

/**
 * Renders the structured container summary used in the modal Overview tab and
 * as the header of the Inspect panel. Single source of truth for the dl/dt/dd
 * shape so badges, formatting, and copy stay consistent across surfaces.
 */
export function ContainerSummaryDL({ card, compact = false }: ContainerSummaryDLProps) {
  return (
    <dl className="container-modal-summary">
      <dt>ID</dt>
      <dd><code className="container-modal-code">{card.id.slice(0, 12)}</code></dd>
      <dt>Image</dt>
      <dd><code className="container-modal-code">{card.image}</code></dd>
      <dt>State</dt>
      <dd><ContainerStateBadge state={card.state} /></dd>
      <dt>Status</dt>
      <dd>{card.status || '—'}</dd>
      {!compact && card.createdUtc && (
        <>
          <dt>Created</dt>
          <dd>{new Date(card.createdUtc).toLocaleString()}</dd>
        </>
      )}
      {!compact && (card.composeProject || card.composeService) && (
        <>
          <dt>Compose</dt>
          <dd>
            {card.composeProject ?? '—'}
            {card.composeService ? ` / ${card.composeService}` : ''}
          </dd>
        </>
      )}
      {card.ports.length > 0 && (
        <>
          <dt>Ports</dt>
          <dd className="container-modal-card-ports">
            {card.ports.map((p, idx) => (
              <span key={idx} className="docker-instances-card-port">
                {p.publicPort ? `${p.publicPort}→` : ''}{p.privatePort}/{p.type}
              </span>
            ))}
          </dd>
        </>
      )}
    </dl>
  )
}
