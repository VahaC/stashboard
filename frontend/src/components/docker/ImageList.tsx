import type { DockerImageInfo } from '@/lib/types'
import { formatBytes } from '@/lib/utils'

/** Strip the `sha256:` prefix and keep a short, recognisable id. */
function shortId(id: string): string {
  const bare = id.startsWith('sha256:') ? id.slice(7) : id
  return bare.slice(0, 12)
}

/**
 * V5.5 — a scrollable list of Docker images, shared by the Storage
 * drill-down modal and the prune-confirmation dialog. Each row shows the
 * repo tag(s) (or `<none>` + short id for a dangling image), the short
 * image id, the creation date, the on-disk size, and dangling / unused
 * badges. Sorted largest-first by the caller.
 */
export function ImageList({ images, emptyText }: { images: DockerImageInfo[]; emptyText: string }) {
  if (images.length === 0) {
    return <p className="docker-image-list__empty">{emptyText}</p>
  }
  return (
    <ul className="docker-image-list">
      {images.map((img) => {
        const tag = img.repoTags.length > 0 ? img.repoTags.join(', ') : '<none>:<none>'
        // An in-use image can't be pruned. Only call it out when it'd
        // otherwise be surprising — i.e. for a dangling image the user
        // expects to disappear. (Tagged running images are in use by design.)
        const showInUse = img.isDangling && !img.isUnused && img.usedByContainers.length > 0
        const usedBy = img.usedByContainers.join(', ')
        return (
          <li key={img.id} className="docker-image-list__row">
            <div className="docker-image-list__main">
              <span className="docker-image-list__tag" title={tag}>{tag}</span>
              <span className="docker-image-list__meta">
                {shortId(img.id)}
                {img.createdUtc ? ` · ${new Date(img.createdUtc).toLocaleDateString()}` : ''}
                {showInUse ? ` · used by ${usedBy}` : ''}
              </span>
            </div>
            <div className="docker-image-list__right">
              {img.isDangling && <span className="docker-image-list__badge docker-image-list__badge--dangling">dangling</span>}
              {showInUse && <span className="docker-image-list__badge docker-image-list__badge--inuse">in use</span>}
              {img.isUnused && !img.isDangling && <span className="docker-image-list__badge docker-image-list__badge--unused">unused</span>}
              <span className="docker-image-list__size">{formatBytes(img.sizeBytes)}</span>
            </div>
          </li>
        )
      })}
    </ul>
  )
}
