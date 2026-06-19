import type { MouseEvent, ReactNode } from 'react'
import { AlertCircle } from 'lucide-react'
import { cn } from '@/lib/utils'
import { StateBadge } from '@/components/shared/StateBadge'
// The card chrome lives in the Docker page stylesheet; import it here so the
// component carries its own styles to any route (Docker, Proxmox, …).
import '@/styles/docker-instances.css'

export interface EntityCardProps {
  /** Raw runtime state — drives `data-container-state` + the state badge. */
  state: string
  /** Primary name (top-left). */
  name: ReactNode
  /** Optional leading avatar rendered before the name (e.g. a container icon).
   *  Omitted ⇒ nothing changes for callers that don't set it (the LXC card). */
  icon?: ReactNode
  /** Extra inline content after the name (e.g. a watch chip). */
  headerExtra?: ReactNode
  /** Rendered before the state badge in the top-right (e.g. an update badge). */
  updateBadge?: ReactNode
  /** Mono sub-line under the name (image for Docker, `CT <id>` for LXC). */
  subtitle?: ReactNode
  subtitleTitle?: string
  /** Status / uptime line. */
  statusLine?: ReactNode
  /** Chip row (ports for Docker, resources for LXC). Pass `cc-chip` spans. */
  chips?: ReactNode
  /** Optional extra meta row below the chips, on its own line — e.g. the LXC
   *  IP address, which is deliberately separated from the resource chips. */
  extraMeta?: ReactNode
  clickable?: boolean
  /** V6.7 — muted treatment for a card whose monitoring is turned off (the
   *  Proxmox LXC disabled state, the analogue of a disabled Docker watch). */
  dimmed?: boolean
  onActivate?: () => void
  onContextMenu?: (e: MouseEvent<HTMLDivElement>) => void
  /** Diagnostics cluster (left of the action row). */
  actionsLeft?: ReactNode
  /** Lifecycle / overflow cluster (right of the action row). */
  actionsRight?: ReactNode
  /** Inline error under the action row. */
  error?: string
  /** Extra nodes (dialogs) rendered inside the card wrapper. */
  children?: ReactNode
}

/**
 * The single, shared container/guest card. Both the Docker `ContainerCard` and
 * the Proxmox LXC card compose this so the two are literally the same surface —
 * same DOM, same `cc-*` / `docker-instances-card-*` classes, same state badge.
 * Callers supply the domain-specific bits (subtitle, chips, action buttons) via
 * slots; the frame, click semantics, and layout live here.
 */
export function EntityCard({
  state, name, icon, headerExtra, updateBadge, subtitle, subtitleTitle,
  statusLine, chips, extraMeta, clickable = true, dimmed = false, onActivate, onContextMenu, actionsLeft, actionsRight, error, children,
}: EntityCardProps) {
  // Only the image + status lines sit beside the icon — they're short and align
  // with the icon's height. The ports row and any extra meta stay full-width
  // below so they aren't squeezed into the narrow column (ports would otherwise
  // truncate). Without an icon everything stays in the body's original single
  // column so the LXC card is untouched.
  const besideIcon = (
    <>
      {subtitle != null && (
        <div className="cc-image docker-instances-card-image" title={subtitleTitle}>
          {subtitle}
        </div>
      )}
      {statusLine != null && (
        <div className="cc-meta docker-instances-card-meta">{statusLine}</div>
      )}
    </>
  )

  const fullWidth = (
    <>
      {chips != null && (
        <div className="cc-meta docker-instances-card-meta docker-instances-card-ports">{chips}</div>
      )}
      {extraMeta != null && (
        <div className="cc-meta docker-instances-card-meta docker-instances-card-ports">{extraMeta}</div>
      )}
    </>
  )

  return (
    <div className={cn('cc docker-instances-card', dimmed && 'cc-dimmed')} data-container-state={state.toLowerCase()} onContextMenu={onContextMenu}>
      <div
        className="cc-body docker-instances-card-body"
        role={clickable ? 'button' : undefined}
        tabIndex={clickable ? 0 : undefined}
        onClick={clickable ? onActivate : undefined}
        onKeyDown={clickable ? (e) => {
          if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onActivate?.() }
        } : undefined}
      >
        <div className="cc-head docker-instances-card-head">
          <span className="cc-name docker-instances-card-name">
            {name}
            {headerExtra}
          </span>
          <div className="cc-head-right">
            {updateBadge}
            <StateBadge state={state} />
          </div>
        </div>

        {icon ? (
          <div className="cc-lead">
            <span className="cc-lead-icon">{icon}</span>
            <div className="cc-lead-content">{besideIcon}</div>
          </div>
        ) : besideIcon}
        {fullWidth}
      </div>

      <div className="cc-rule" aria-hidden />
      <div className="cc-actions docker-instances-card-actions" onClick={(e) => e.stopPropagation()}>
        <div className="cc-actions-secondary">{actionsLeft}</div>
        <div className="cc-actions-right">{actionsRight}</div>
      </div>

      {error && (
        <p className="cc-action-error docker-instances-card-action-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}

      {children}
    </div>
  )
}
