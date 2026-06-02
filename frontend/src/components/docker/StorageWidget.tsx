import { useMemo, useState } from 'react'
import { Trash2, HardDrive, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/components/ui/dialog'
import { useDockerImageStorage, usePruneImages } from '@/lib/queries'
import type { DockerImageInfo, DockerImageStorage, DockerPruneRun } from '@/lib/types'
import { formatBytes, getApiErrorMessage } from '@/lib/utils'
import { ImageList } from './ImageList'

interface StorageWidgetProps {
  connectionId: string
  /** Whether this connection has opted in to the aggressive "also prune
   *  unused images" scope. Defaults the dialog's checkbox. */
  defaultPruneUnused: boolean
}

/** Which subset of images the drill-down modal is showing. */
type DetailScope = 'all' | 'dangling' | 'unused'

const SCOPE_TITLE: Record<DetailScope, string> = {
  all: 'All images',
  dangling: 'Dangling images',
  unused: 'Unused images',
}

const SCOPE_EMPTY: Record<DetailScope, string> = {
  all: 'No images on this host.',
  dangling: 'No dangling images — nothing to clean up.',
  unused: 'No unused images.',
}

function scopeImages(images: DockerImageInfo[], scope: DetailScope): DockerImageInfo[] {
  if (scope === 'dangling') return images.filter((i) => i.isDangling)
  if (scope === 'unused') return images.filter((i) => i.isUnused)
  return images
}

/**
 * V5.5 — Storage widget on the V3.5 Docker instances page. Shows the
 * host's image count, dangling count, and a manual **Prune** button with a
 * dry-run preview dialog that lists exactly which images will be removed.
 * Each stat (Total / Dangling / Unused) is clickable and opens a modal
 * listing those images so the user can see what's actually on the host.
 */
export function StorageWidget({ connectionId, defaultPruneUnused }: StorageWidgetProps) {
  const storage = useDockerImageStorage(connectionId)
  const prune = usePruneImages(connectionId)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [includeUnused, setIncludeUnused] = useState(defaultPruneUnused)
  const [lastRun, setLastRun] = useState<DockerPruneRun | null>(null)
  const [pruneError, setPruneError] = useState<string | null>(null)
  const [detailScope, setDetailScope] = useState<DetailScope | null>(null)

  const data = storage.data
  const danglingLine = data
    ? `${data.danglingImageCount} dangling (${formatBytes(data.danglingImageBytes)})`
    : '…'
  const unusedLine = data
    ? `${data.unusedImageCount} unused (${formatBytes(data.unusedImageBytes)})`
    : '…'

  const detailImages = useMemo(
    () => (data && detailScope ? scopeImages(data.images, detailScope) : []),
    [data, detailScope],
  )

  const handleConfirm = async () => {
    setPruneError(null)
    try {
      const run = await prune.mutateAsync({ includeUnused })
      setLastRun(run)
      if (run.status !== 'Success' && run.status !== 'NothingToPrune') {
        setPruneError(run.error ?? 'Prune failed.')
      }
    } catch (err) {
      setPruneError(getApiErrorMessage(err, 'Prune failed.'))
    }
  }

  const handleClose = () => {
    if (prune.isPending) return
    setConfirmOpen(false)
    setLastRun(null)
    setPruneError(null)
    setIncludeUnused(defaultPruneUnused)
  }

  return (
    <div className="docker-storage-widget">
      <div className="docker-storage-widget__header">
        <HardDrive className="h-4 w-4" />
        <span className="docker-storage-widget__title">Storage</span>
        <button
          type="button"
          className="docker-storage-widget__refresh ui-button ui-button-outline ui-button-size-sm"
          onClick={() => void storage.refetch()}
          aria-label="Refresh storage summary"
          disabled={storage.isFetching}
        >
          <RefreshCw className={`h-3.5 w-3.5${storage.isFetching ? ' docker-storage-widget__refresh--spin' : ''}`} />
          Refresh
        </button>
        <div className="docker-storage-widget__actions">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => setConfirmOpen(true)}
            disabled={!data || storage.isLoading}
          >
            <Trash2 className="h-3.5 w-3.5" />
            Prune
          </Button>
        </div>
      </div>
      {storage.isError ? (
        <div className="docker-storage-widget__error">
          {getApiErrorMessage(storage.error, 'Could not load image storage.')}
        </div>
      ) : (
        <dl className="docker-storage-widget__stats">
          <div>
            <dt>Total</dt>
            <dd>
              <button
                type="button"
                className="docker-storage-widget__stat-button"
                disabled={!data}
                onClick={() => setDetailScope('all')}
              >
                {data ? `${data.totalImageCount} images` : '…'}
              </button>
            </dd>
          </div>
          <div>
            <dt>Dangling</dt>
            <dd>
              <button
                type="button"
                className="docker-storage-widget__stat-button"
                disabled={!data}
                onClick={() => setDetailScope('dangling')}
              >
                {danglingLine}
              </button>
            </dd>
          </div>
          <div>
            <dt>Unused</dt>
            <dd>
              <button
                type="button"
                className="docker-storage-widget__stat-button"
                disabled={!data}
                onClick={() => setDetailScope('unused')}
              >
                {unusedLine}
              </button>
            </dd>
          </div>
          <div>
            <dt>Last prune</dt>
            <dd>{data?.lastPruneUtc ? new Date(data.lastPruneUtc).toLocaleString() : 'Never'}</dd>
          </div>
        </dl>
      )}

      {/* Drill-down: which images make up the clicked stat. */}
      <Dialog open={detailScope !== null} onOpenChange={(open) => { if (!open) setDetailScope(null) }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {detailScope ? SCOPE_TITLE[detailScope] : ''}
              {data && detailScope && (
                <span className="docker-storage-widget__detail-count">
                  {' '}— {detailImages.length} image{detailImages.length === 1 ? '' : 's'},{' '}
                  {formatBytes(detailImages.reduce((sum, i) => sum + i.sizeBytes, 0))}
                </span>
              )}
            </DialogTitle>
          </DialogHeader>
          <ImageList images={detailImages} emptyText={detailScope ? SCOPE_EMPTY[detailScope] : ''} />
          <DialogFooter>
            <Button type="button" onClick={() => setDetailScope(null)}>Close</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={confirmOpen} onOpenChange={(open) => { if (!open) handleClose() }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Prune images?</DialogTitle>
          </DialogHeader>
          <PruneDialogBody
            storage={data}
            includeUnused={includeUnused}
            setIncludeUnused={setIncludeUnused}
            lastRun={lastRun}
            error={pruneError}
            inFlight={prune.isPending}
          />
          <DialogFooter>
            {lastRun ? (
              <Button type="button" onClick={handleClose}>Close</Button>
            ) : (
              <>
                <Button type="button" variant="outline" onClick={handleClose} disabled={prune.isPending}>
                  Cancel
                </Button>
                <Button type="button" onClick={handleConfirm} disabled={prune.isPending || !data}>
                  {prune.isPending ? 'Pruning…' : 'Prune now'}
                </Button>
              </>
            )}
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

interface PruneDialogBodyProps {
  storage: DockerImageStorage | undefined
  includeUnused: boolean
  setIncludeUnused: (value: boolean) => void
  lastRun: DockerPruneRun | null
  error: string | null
  inFlight: boolean
}

function PruneDialogBody({ storage, includeUnused, setIncludeUnused, lastRun, error, inFlight }: PruneDialogBodyProps) {
  // The images a prune will ACTUALLY remove. Docker never deletes an image
  // referenced by a container (running or stopped), so an in-use image is
  // never removed regardless of scope:
  //   - dangling scope  → dangling AND not in use
  //   - unused scope (-a) → every image not referenced by a container
  const targetImages = useMemo(() => {
    const images = storage?.images ?? []
    return includeUnused
      ? images.filter((i) => i.isUnused)
      : images.filter((i) => i.isDangling && i.isUnused)
  }, [storage, includeUnused])

  // Dangling images that survive because a container still references them —
  // the case that confuses people ("it says 1 dangling but prune removed 0").
  const skippedInUse = useMemo(
    () => (storage?.images ?? []).filter((i) => i.isDangling && !i.isUnused),
    [storage],
  )

  if (lastRun) {
    const ok = lastRun.status === 'Success' || lastRun.status === 'NothingToPrune'
    return (
      <div className="docker-storage-widget__result">
        <p>
          {ok
            ? `Removed ${lastRun.imagesDeleted} image${lastRun.imagesDeleted === 1 ? '' : 's'} — freed ${formatBytes(lastRun.spaceReclaimedBytes)}.`
            : `Prune failed: ${lastRun.error ?? lastRun.status}.`}
        </p>
        {lastRun.includedUnused && ok && (
          <p className="docker-storage-widget__hint">
            Scope: dangling + unused images.
          </p>
        )}
      </div>
    )
  }

  const targetBytes = targetImages.reduce((sum, i) => sum + i.sizeBytes, 0)

  return (
    <div className="docker-storage-widget__confirm">
      <p>
        Stashboard will run <code>docker image prune</code> on this host.
        {' '}This is destructive — the deleted images cannot be brought back without re-pulling.
      </p>
      <p className="docker-storage-widget__preview">
        <strong>Will remove:</strong> {targetImages.length} image{targetImages.length === 1 ? '' : 's'} {' '}
        (~{formatBytes(targetBytes)})
      </p>
      <ImageList
        images={targetImages}
        emptyText={includeUnused ? 'No unused images to remove.' : 'No dangling images to remove.'}
      />
      {skippedInUse.length > 0 && (
        <p className="docker-storage-widget__warn">
          {skippedInUse.length} dangling image{skippedInUse.length === 1 ? '' : 's'}{' '}
          ({formatBytes(skippedInUse.reduce((sum, i) => sum + i.sizeBytes, 0))}) can't be removed —
          still used by {skippedInUse.flatMap((i) => i.usedByContainers).join(', ') || 'a container'}.
          Remove that container first to reclaim the space.
        </p>
      )}
      <label className="docker-storage-widget__checkbox">
        <input
          type="checkbox"
          checked={includeUnused}
          onChange={(e) => setIncludeUnused(e.target.checked)}
          disabled={inFlight}
        />
        <span>
          Also prune <strong>unused</strong> images (anything not referenced by a running or stopped container).
        </span>
      </label>
      <p className="docker-storage-widget__hint">
        Unused includes images you might still want for a "rollback to previous tag" workflow.
      </p>
      {error && <p className="docker-storage-widget__error">{error}</p>}
    </div>
  )
}
