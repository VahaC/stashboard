import { useState } from 'react'
import { Box } from 'lucide-react'
import { cn } from '@/lib/utils'

/**
 * V7.8 — the rounded avatar shown before a container's name on its card. Renders
 * the resolved icon (custom upload or official dashboard-icon) when one exists,
 * otherwise a placeholder: a Box glyph plus the name's leading initials. Mirrors
 * the dashboard `service-card-logo` fallback treatment so the two surfaces match.
 */
export function ContainerIcon({
  dataUri,
  name,
  className,
}: {
  dataUri: string | null
  name: string
  className?: string
}) {
  // A broken/expired data URI shouldn't leave an empty box — fall back to the
  // placeholder if the image fails to decode.
  const [broken, setBroken] = useState(false)
  const showImage = Boolean(dataUri) && !broken

  return (
    <span className={cn('cc-card-icon', className)} aria-hidden>
      {showImage ? (
        <img src={dataUri!} alt="" className="cc-card-icon-img" onError={() => setBroken(true)} />
      ) : (
        <span className="cc-card-icon-fallback">
          {initials(name) || <Box className="h-3.5 w-3.5" />}
        </span>
      )}
    </span>
  )
}

function initials(name: string): string {
  const cleaned = name.replace(/^\//, '').trim()
  if (!cleaned) return ''
  const parts = cleaned.split(/[-_\s.]+/).filter(Boolean)
  const letters = (parts.length >= 2 ? parts[0][0] + parts[1][0] : cleaned.slice(0, 2))
  return letters.toUpperCase()
}
