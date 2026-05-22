import type { DockerUpdateStatus } from '@/lib/types'
import { resolveDockerUpdateStatus } from '@/lib/types'
import { cn } from '@/lib/utils'

export type UpdateStatusBadgeProps = {
  status: DockerUpdateStatus
  /** Override the default label. */
  label?: string
  title?: string
  className?: string
}

const DEFAULT_LABELS: Record<string, string> = {
  UpdateAvailable: 'Update',
  UpToDate: 'Up to date',
  Error: 'Error',
  Disabled: 'Disabled',
  Unknown: 'Unknown',
}

/**
 * Pill badge for the watch update status. Replaces both the inline
 * `.docker-section-badge[data-status]` chips and the ad-hoc spans that
 * coloured "Update" / "Up to date" elsewhere in the UI.
 */
export function UpdateStatusBadge({ status, label, title, className }: UpdateStatusBadgeProps) {
  const resolved = resolveDockerUpdateStatus(status)

  if (resolved === 'UpToDate' && !label) {
    // Don't show a badge if it's up to date and no custom label is provided.
    return null
  }
  return (
    <span
      className={cn('docker-section-badge', className)}
      data-status={resolved}
      title={title}
    >
      {label ?? DEFAULT_LABELS[resolved] ?? resolved}
    </span>
  )
}
