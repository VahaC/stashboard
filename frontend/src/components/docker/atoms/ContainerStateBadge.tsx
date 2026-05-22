import { cn } from '@/lib/utils'

export type ContainerStateBadgeProps = {
  /** Raw state string from the Docker API: running, exited, restarting, paused, created, dead, etc. */
  state: string
  size?: 'sm' | 'md'
  className?: string
}

/**
 * Pill badge for a container's runtime state. Single source of truth for the
 * coloured chip we render next to a container name on the Docker instances
 * page, inside the modal header, and in the Overview tab summary list.
 */
export function ContainerStateBadge({ state, size, className }: ContainerStateBadgeProps) {
  const normalized = (state ?? '').toLowerCase() || 'unknown'
  const label = state || 'unknown'
  return (
    <span
      className={cn('docker-instances-card-state', size === 'sm' && 'cc-state-sm', className)}
      data-state={normalized}
    >
      {label}
    </span>
  )
}
