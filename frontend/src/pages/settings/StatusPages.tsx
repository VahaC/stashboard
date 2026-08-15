import { useMemo, useState } from 'react'
import { Check, Copy, ExternalLink, Globe, Pencil, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { useDeleteStatusPage, useSaveStatusPage, useServices, useStatusPages } from '@/lib/queries'
import { copyToClipboard, parseApiErrors } from '@/lib/utils'
import type { Service, StatusPage } from '@/lib/types'
import '@/styles/account-page.css'
import '@/styles/status-pages.css'

/**
 * V10.2 — Settings → Status pages. Owner-side management for the public, read-only status
 * pages: create / edit / publish / delete a named selection of the user's own services, and
 * copy the public link. The public view itself lives at `/status/{slug}` and needs no account.
 */
export function StatusPages() {
  const { data: pages = [] } = useStatusPages()
  const del = useDeleteStatusPage()
  const [editing, setEditing] = useState<StatusPage | null>(null)
  const [creating, setCreating] = useState(false)
  const [copied, setCopied] = useState<string | null>(null)

  const copyLink = async (slug: string) => {
    const ok = await copyToClipboard(publicUrl(slug))
    if (ok) {
      setCopied(slug)
      setTimeout(() => setCopied((s) => (s === slug ? null : s)), 1500)
    } else {
      // Last-resort fallback so the user can still grab the link by hand.
      window.prompt('Copy this public link:', publicUrl(slug))
    }
  }

  return (
    <div className="account-page account-stack">
      <div className="status-pages-head">
        <h1 className="text-2xl font-semibold">Status pages</h1>
        <Button onClick={() => setCreating(true)}>
          <Plus className="h-4 w-4" /> New status page
        </Button>
      </div>

      <p className="status-pages-intro">
        Publish a read-only page showing the live status and uptime history of a hand-picked set of
        your services — shareable with family or teammates, no account required. A page is a{' '}
        <strong>draft</strong> until you switch <strong>Published</strong> on; an unpublished link
        returns “not found”. The public view only ever shows the display name, status and uptime —
        never your URLs, credentials, notes, categories or Docker/Proxmox details.
      </p>

      <div className="status-pages-list">
        {pages.length === 0 && (
          <p className="manage-empty">No status pages yet. Create one to share your services’ status.</p>
        )}
        {pages.map((page) => (
          <div key={page.id} className="status-page-row">
            <div className="status-page-row-main">
              <span className="status-page-row-title">
                <Globe className="h-4 w-4" />
                {page.title}
              </span>
              <span className="status-page-row-slug">/status/{page.slug}</span>
              <span className="status-page-row-meta">
                {page.items.length} service{page.items.length === 1 ? '' : 's'}
              </span>
            </div>
            <div className="status-page-row-side">
              <span
                className="status-page-badge"
                data-published={page.isPublished}
              >
                {page.isPublished ? 'Published' : 'Draft'}
              </span>
              <Button variant="ghost" size="icon" title="Copy public link" onClick={() => copyLink(page.slug)}>
                {copied === page.slug ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
              </Button>
              <Button
                variant="ghost"
                size="icon"
                title="Open public page"
                disabled={!page.isPublished}
                onClick={() => window.open(publicUrl(page.slug), '_blank', 'noopener')}
              >
                <ExternalLink className="h-3.5 w-3.5" />
              </Button>
              <Button variant="ghost" size="icon" title="Edit" onClick={() => setEditing(page)}>
                <Pencil className="h-3.5 w-3.5" />
              </Button>
              <Button
                variant="ghost"
                size="icon"
                title="Delete"
                onClick={() => {
                  if (confirm(`Delete status page “${page.title}”? The public link will stop working.`))
                    del.mutate(page.id)
                }}
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </div>
          </div>
        ))}
      </div>

      {(creating || editing) && (
        <StatusPageEditor
          page={editing}
          onClose={() => {
            setCreating(false)
            setEditing(null)
          }}
        />
      )}
    </div>
  )
}

function StatusPageEditor({ page, onClose }: { page: StatusPage | null; onClose: () => void }) {
  const { data: services = [] } = useServices()
  const save = useSaveStatusPage()

  const sortedServices = useMemo(
    () => [...services].sort((a, b) => a.name.localeCompare(b.name)),
    [services],
  )

  const [title, setTitle] = useState(page?.title ?? '')
  const [description, setDescription] = useState(page?.description ?? '')
  const [slug, setSlug] = useState(page?.slug ?? '')
  const [slugTouched, setSlugTouched] = useState(Boolean(page))
  const [isPublished, setIsPublished] = useState(page?.isPublished ?? false)
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(page?.items.map((i) => i.webResourceId) ?? []),
  )
  const [displayNames, setDisplayNames] = useState<Record<string, string>>(
    () => Object.fromEntries((page?.items ?? []).map((i) => [i.webResourceId, i.displayName ?? ''])),
  )
  const [error, setError] = useState<string | null>(null)

  // While creating and the slug hasn't been hand-edited, mirror the title.
  const effectiveSlug = slugTouched ? slug : localSlugify(title)

  const toggle = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const submit = async () => {
    setError(null)
    const items = sortedServices
      .filter((s) => selected.has(s.id))
      .map((s) => ({
        webResourceId: s.id,
        displayName: displayNames[s.id]?.trim() ? displayNames[s.id].trim() : null,
      }))
    try {
      await save.mutateAsync({
        id: page?.id,
        data: {
          title: title.trim(),
          description: description.trim() ? description.trim() : null,
          slug: effectiveSlug.trim() ? effectiveSlug.trim() : null,
          isPublished,
          items,
        },
      })
      onClose()
    } catch (e: unknown) {
      const { globalError } = parseApiErrors(e)
      setError(globalError ?? 'Failed to save the status page.')
    }
  }

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="status-page-editor">
        <DialogHeader>
          <DialogTitle>{page ? 'Edit status page' : 'New status page'}</DialogTitle>
          <DialogDescription>
            Pick the services to show, give it a public link, and publish when you’re ready.
          </DialogDescription>
        </DialogHeader>

        <div className="status-page-editor-body">
          <div className="account-field">
            <label className="account-form-label" htmlFor="sp-title">Title</label>
            <Input id="sp-title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="My homelab status" />
          </div>

          <div className="account-field">
            <label className="account-form-label" htmlFor="sp-desc">Description (optional)</label>
            <Textarea
              id="sp-desc"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Shown under the title on the public page."
              rows={2}
            />
          </div>

          <div className="account-field">
            <label className="account-form-label" htmlFor="sp-slug">Public link</label>
            <div className="status-page-slug-row">
              <span className="status-page-slug-prefix">/status/</span>
              <Input
                id="sp-slug"
                value={effectiveSlug}
                onChange={(e) => {
                  setSlugTouched(true)
                  setSlug(localSlugify(e.target.value))
                }}
                placeholder="my-homelab"
              />
            </div>
            <p className="text-xs text-[var(--muted-foreground)]">
              Lowercase letters, numbers and hyphens. Leave it to auto-fill from the title.
            </p>
          </div>

          <label className="account-checkbox-label">
            <input type="checkbox" checked={isPublished} onChange={(e) => setIsPublished(e.target.checked)} />
            Published (the public link is live while this is on)
          </label>

          <div className="account-field">
            <label className="account-form-label">Services on this page</label>
            {sortedServices.length === 0 ? (
              <p className="manage-empty">You have no services yet.</p>
            ) : (
              <div className="status-page-service-picker">
                {sortedServices.map((s) => (
                  <ServicePickRow
                    key={s.id}
                    service={s}
                    selected={selected.has(s.id)}
                    displayName={displayNames[s.id] ?? ''}
                    onToggle={() => toggle(s.id)}
                    onDisplayName={(v) => setDisplayNames((p) => ({ ...p, [s.id]: v }))}
                  />
                ))}
              </div>
            )}
          </div>

          {error && <p className="account-form-error">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button onClick={submit} disabled={!title.trim() || save.isPending}>
            {save.isPending ? 'Saving…' : page ? 'Save changes' : 'Create page'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function ServicePickRow({
  service,
  selected,
  displayName,
  onToggle,
  onDisplayName,
}: {
  service: Service
  selected: boolean
  displayName: string
  onToggle: () => void
  onDisplayName: (value: string) => void
}) {
  return (
    <div className="status-page-service-row" data-selected={selected}>
      <label className="status-page-service-check">
        <input type="checkbox" checked={selected} onChange={onToggle} />
        <span className="status-page-service-name">{service.name}</span>
      </label>
      {selected && (
        <Input
          value={displayName}
          onChange={(e) => onDisplayName(e.target.value)}
          placeholder={`Public name (default: ${service.name})`}
          className="status-page-service-display"
        />
      )}
    </div>
  )
}

function publicUrl(slug: string): string {
  return `${window.location.origin}/status/${slug}`
}

/** Mirror of the server's slug rules for the live preview / auto-fill. */
function localSlugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)
    .replace(/-+$/g, '')
}
