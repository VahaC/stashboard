import { cn } from '@/lib/utils'

export type StateBadgeProps = {
  /** Raw state string: running, exited, stopped, restarting, paused, created, dead, etc. */
  state: string
  size?: 'sm' | 'md'
  className?: string
}

/**
 * Shared runtime-state pill used by both the Docker container surfaces and the
 * Proxmox LXC surfaces (card + modal header + overview). Single source of truth
 * for the coloured chip; the colour comes from `data-state` rules in
 * `docker-instances.css` (which the consuming components import).
 */
export function StateBadge({ state, size, className }: StateBadgeProps) {
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
