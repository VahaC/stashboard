import { MoreHorizontal, Play, RefreshCw, Square, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

export type LifecycleAction = 'start' | 'stop' | 'restart' | 'remove'

export type ContainerLifecycleActionsProps = {
  containerName: string
  running: boolean
  /** True while Docker is auto-restarting the container in a crash loop.
   *  Restart is hidden in this state — it would just duplicate what the
   *  restart policy is already doing; only Stop is meaningful. */
  restarting?: boolean
  allowRemoval: boolean
  busy: boolean
  size?: 'sm' | 'default'
  /** `full` shows full labels; `compact` collapses Restart to icon-only and
   *  pushes Remove behind a `⋯` overflow button. */
  density?: 'full' | 'compact'
  className?: string
  onAction: (action: LifecycleAction) => void | Promise<void>
  /** V5.0 — called when the user clicks Remove. The parent is responsible
   *  for showing the confirmation dialog and calling `onAction('remove')`
   *  only after the user confirms. */
  onRemoveRequest?: () => void
}

export function ContainerLifecycleActions({
  containerName: _containerName, running, restarting = false, allowRemoval, busy, size = 'sm', density = 'full', className, onAction, onRemoveRequest,
}: ContainerLifecycleActionsProps) {
  const compact = density === 'compact'
  const remove = () => {
    if (onRemoveRequest) {
      onRemoveRequest()
    } else {
      void onAction('remove')
    }
  }

  return (
    <div className={cn('cc-lifecycle', className)}>
      {running ? (
        <>
          <Button type="button" variant="outline" size={size} disabled={busy} onClick={() => void onAction('stop')}>
            <Square className="h-3.5 w-3.5" />
            <span className="label-text">Stop</span>
          </Button>
          {!restarting && (
            <Button
              type="button"
              variant="outline"
              size={size}
              disabled={busy}
              onClick={() => void onAction('restart')}
              title="Restart container"
              aria-label="Restart container"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              {!compact && <span className="label-text">Restart</span>}
            </Button>
          )}
        </>
      ) : (
        <Button type="button" variant="outline" size={size} disabled={busy} onClick={() => void onAction('start')}>
          <Play className="h-3.5 w-3.5" />
          <span className="label-text">Start</span>
        </Button>
      )}
      {allowRemoval && !compact && (
        <Button
          type="button"
          variant="ghost"
          size={size}
          disabled={busy}
          onClick={remove}
          title="Remove container (destructive)"
        >
          <Trash2 className="h-3.5 w-3.5" />
          <span className="label-text">Remove</span>
        </Button>
      )}
      {allowRemoval && compact && (
        <details className="cc-overflow">
          <summary aria-label="More actions">
            <MoreHorizontal className="h-3.5 w-3.5" />
          </summary>
          <div className="cc-overflow-menu">
            <button
              type="button"
              className="cc-overflow-item cc-overflow-item-danger"
              disabled={busy}
              onClick={remove}
            >
              <Trash2 className="h-3.5 w-3.5" /> Remove
            </button>
          </div>
        </details>
      )}
    </div>
  )
}
