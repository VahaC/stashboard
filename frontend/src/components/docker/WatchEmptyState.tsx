import { Button } from '@/components/ui/button'

export type WatchEmptyStateProps = {
  containerName: string
  onStartTracking: () => void
}

/**
 * V3.6 — empty state for the Watch tab on an untracked container. Containers
 * are first-class now, so tracking no longer requires a service — the user can
 * start tracking straight from the Docker page and optionally link a service
 * inside the form.
 */
export function WatchEmptyState({ containerName, onStartTracking }: WatchEmptyStateProps) {
  return (
    <div className="cm-panel">
      <p>
        <strong>{containerName}</strong> isn't tracked for image updates yet. Tracking watches
        the registry on a schedule and notifies you when a new digest appears — you can
        optionally link it to a service.
      </p>
      <div className="cm-actions">
        <Button type="button" size="sm" onClick={onStartTracking}>
          Track this container
        </Button>
      </div>
    </div>
  )
}
