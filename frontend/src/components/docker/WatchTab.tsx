import { useMemo, useState } from 'react'
import type { DockerContainerCard, DockerWatch, Service } from '@/lib/types'
import { useCheckConnectionWatch, useConnectionWatches, useDeleteConnectionWatch } from '@/lib/queries'
import { DockerWatchForm } from '@/components/DockerWatchSection'
import { WatchEmptyState } from './WatchEmptyState'

export type WatchTabProps = {
  connectionId: string
  card: DockerContainerCard
  /** Optional service context — when the modal is opened from a service it
   *  pre-selects that service as the link target for a new watch. */
  serviceContext?: { service: Service; watch: DockerWatch | null }
}

/**
 * V3.6 — the Watch tab inside the container modal. Containers are first-class
 * now, so tracking is created/edited directly against the connection here,
 * with an optional service link picked inside the form. No service is required
 * to start tracking.
 */
export function WatchTab({ connectionId, card, serviceContext }: WatchTabProps) {
  const watches = useConnectionWatches(connectionId)
  const checkNow = useCheckConnectionWatch(connectionId)
  const deleteWatch = useDeleteConnectionWatch(connectionId)
  const [creating, setCreating] = useState(false)

  // Resolve the watch for this container by name (the natural key per host).
  const existing = useMemo<DockerWatch | null>(() => {
    if (serviceContext?.watch && serviceContext.watch.containerName === card.name) {
      return serviceContext.watch
    }
    return watches.data?.find((w) => w.containerName === card.name) ?? null
  }, [watches.data, serviceContext, card.name])

  if (watches.isLoading && !existing) {
    return <p className="cm-empty">Loading…</p>
  }

  if (!existing && !creating) {
    return <WatchEmptyState containerName={card.name} onStartTracking={() => setCreating(true)} />
  }

  const handleDelete = async () => {
    if (!existing) return
    if (!confirm(`Stop tracking "${existing.label || existing.containerName}" for updates?`)) return
    try {
      await deleteWatch.mutateAsync(existing.id)
    } catch { /* surfaced via mutation state */ }
  }

  return (
    <DockerWatchForm
      connectionId={connectionId}
      existing={existing}
      defaultServiceId={serviceContext?.service.id ?? null}
      defaultContainerName={card.name}
      defaultImageReference={card.image}
      onCheckNow={existing ? async () => { await checkNow.mutateAsync(existing.id) } : undefined}
      onDelete={existing ? handleDelete : undefined}
      onCancelNew={creating ? () => setCreating(false) : undefined}
      onCreated={() => setCreating(false)}
      checkInFlight={checkNow.isPending}
      hideContainerPanels
      proxmoxLink={card.proxmoxLink}
    />
  )
}
