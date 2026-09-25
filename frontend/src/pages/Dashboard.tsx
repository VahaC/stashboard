import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Activity, ChevronDown, ChevronRight, Container, GripVertical, History, Info, KeyRound, MoreVertical, Plus, RefreshCw, Search, Server } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useCategories, useCheckNow, useServices } from '@/lib/queries'
import { ServiceModal, type ModalTab } from '@/components/ServiceModal'
import { FloatingMenu } from '@/components/shared/FloatingMenu'
import { resolveDockerUpdateStatus, type DashboardSortMode, type Service, type ServiceStatus } from '@/lib/types'
import { accountApi } from '@/lib/account-api'
import { usePointerSort, type PointerSort } from '@/lib/use-pointer-sort'
import { cn } from '@/lib/utils'
import '@/styles/dashboard.css'

/** V10.6 — drag props threaded into a card / group header when Custom sort is active. */
interface CardDrag {
  id: string
  dragging: boolean
  onPointerDown: (event: React.PointerEvent) => void
  /** Returns true (once) when the press that just ended was a drag, so `onClick` can
   *  swallow the click after a drop while a plain click still opens the card. */
  wasDragged: () => boolean
}

/** Tabs surfaced in the card's "⋮" menu, mirroring the order + labels of the
 *  ServiceModal tab strip so the menu and the modal stay in lockstep. */
const SERVICE_TABS: ReadonlyArray<[ModalTab, string, typeof Info]> = [
  ['general', 'General', Info],
  ['healthcheck', 'Healthcheck', Activity],
  ['uptime', 'Uptime', History],
  ['credentials', 'Credentials', KeyRound],
  ['docker', 'Docker', Container],
  ['proxmox', 'Proxmox', Server],
]

const resolveStatus = (s: ServiceStatus) =>
  typeof s === 'number' ? (['Unknown', 'Up', 'Down', 'NeedsAttention'][s] as string) : s

const statusDotClass = (s: ServiceStatus) => {
  const v = resolveStatus(s)
  if (v === 'Up') return 'bg-[var(--status-up)]'
  if (v === 'Down') return 'bg-[var(--status-down)]'
  if (v === 'NeedsAttention') return 'bg-[var(--status-attention)]'
  return 'bg-[var(--status-unknown)]'
}

const statusTitle = (s: Service) => {
  const v = resolveStatus(s.currentStatus)
  if (v === 'Up') return `Up — ${s.lastResponseTimeMs ?? 0} ms`
  if (v === 'NeedsAttention') return `Available, but needs attention — ${s.lastError ?? 'certificate validation was ignored'}`
  if (v === 'Down') return `Down — ${s.lastError ?? 'unknown error'}`
  return 'Not checked yet'
}

const normalizeCategoryName = (service: Service) => service.categoryName?.trim() || 'Uncategorized'

const initials = (name: string) =>
  name.split(/\s+/).slice(0, 2).map((w) => w[0]?.toUpperCase() ?? '').join('')

export function Dashboard() {
  const { data: services = [], isLoading } = useServices()
  const { data: categories = [] } = useCategories()
  const [searchParams, setSearchParams] = useSearchParams()
  const [search, setSearch] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('')
  const [sortMode, setSortMode] = useState<DashboardSortMode>('name')
  const [groupByCategory, setGroupByCategory] = useState(false)
  const [collapsedGroups, setCollapsedGroups] = useState<Record<string, boolean>>({})
  const [editingId, setEditingId] = useState<string | null>(null)
  const [modalInitialTab, setModalInitialTab] = useState<ModalTab>('general')
  const [modalOpen, setModalOpen] = useState(false)
  const [modalKey, setModalKey] = useState(0)
  const [handledDeepLinkServiceId, setHandledDeepLinkServiceId] = useState<string | null>(null)
  // The card's "⋮" action menu: which service + where to anchor it.
  const [cardMenu, setCardMenu] = useState<{ service: Service; x: number; y: number } | null>(null)
  const checkNow = useCheckNow()

  const deepLinkServiceId = searchParams.get('service')

  const editingService = editingId ? (services.find((s) => s.id === editingId) ?? null) : null

  /* eslint-disable react-hooks/set-state-in-effect -- syncing UI state from the URL deep link (external system) */
  useEffect(() => {
    if (!deepLinkServiceId) return
    if (deepLinkServiceId === handledDeepLinkServiceId) return
    if (isLoading) return

    if (deepLinkServiceId === 'new') {
      setHandledDeepLinkServiceId(deepLinkServiceId)
      setEditingId(null)
      setModalInitialTab('general')
      setModalKey((k) => k + 1)
      setModalOpen(true)

      const next = new URLSearchParams(searchParams)
      next.delete('service')
      setSearchParams(next, { replace: true })
      return
    }

    const target = services.find((s) => s.id === deepLinkServiceId)
    setHandledDeepLinkServiceId(deepLinkServiceId)
    if (!target) return

    setEditingId(target.id)
    setModalInitialTab('general')
    setModalKey((k) => k + 1)
    setModalOpen(true)

    const next = new URLSearchParams(searchParams)
    next.delete('service')
    setSearchParams(next, { replace: true })
  }, [
    deepLinkServiceId,
    handledDeepLinkServiceId,
    isLoading,
    searchParams,
    services,
    setSearchParams,
  ])
  /* eslint-enable react-hooks/set-state-in-effect */

  const filtered = useMemo(() => {
    const loweredSearch = search.toLowerCase()
    return services
      .filter((s) => {
        if (loweredSearch && !s.name.toLowerCase().includes(loweredSearch) && !s.mainUrl.toLowerCase().includes(loweredSearch) && !(s.additionalUrl?.toLowerCase().includes(loweredSearch))) return false
        if (categoryFilter && s.categoryId !== categoryFilter) return false
        return true
      })
      .sort((left, right) => {
        // Custom mode uses the explicit global order; grouping re-sorts within groups.
        if (sortMode === 'custom') return left.sortOrder - right.sortOrder
        if (sortMode === 'category') {
          const categoryCompare = normalizeCategoryName(left).localeCompare(normalizeCategoryName(right))
          if (categoryCompare !== 0) return categoryCompare
        }
        return left.name.localeCompare(right.name)
      })
  }, [services, search, categoryFilter, sortMode])

  const grouped = useMemo(() => {
    const groups = new Map<string, { categoryId: string | null; services: Service[] }>()
    for (const service of filtered) {
      const key = normalizeCategoryName(service)
      const group = groups.get(key) ?? { categoryId: service.categoryId, services: [] }
      group.services.push(service)
      groups.set(key, group)
    }
    let entries = [...groups.entries()].map(([name, g]) => ({ name, categoryId: g.categoryId, services: g.services }))

    if (sortMode === 'custom') {
      // Two independent Custom orders: cards within a group by SortOrderInCategory,
      // groups by the category's own SortOrder — "Uncategorized" always last.
      const groupOrder = new Map(categories.map((c) => [c.id, c.sortOrder]))
      for (const entry of entries) entry.services = [...entry.services].sort((a, b) => a.sortOrderInCategory - b.sortOrderInCategory)
      entries = entries.sort((a, b) => {
        if (a.categoryId === null) return 1
        if (b.categoryId === null) return -1
        return (groupOrder.get(a.categoryId) ?? 0) - (groupOrder.get(b.categoryId) ?? 0)
      })
    }
    return entries
  }, [filtered, sortMode, categories])

  // V10.6 — Custom drag-and-drop ordering. Each commit persists the new order; the
  // pointer-sort hooks keep the on-screen order optimistic so there's no refetch flash.
  const isCustom = sortMode === 'custom'
  const serviceSort = usePointerSort(
    filtered.map((s) => s.id),
    (ids) => { accountApi.setServiceOrder(ids).catch(() => undefined) },
  )
  const groupSort = usePointerSort(
    grouped.filter((g) => g.categoryId).map((g) => g.categoryId as string),
    (ids) => { accountApi.setCategoryOrder(ids).catch(() => undefined) },
    'data-group-sort-id',
  )
  const commitCardsInCategory = (categoryId: string | null, ids: string[]) => {
    accountApi.setServiceOrderInCategory(categoryId, ids).catch(() => undefined)
  }

  const openNew = () => { setEditingId(null); setModalInitialTab('general'); setModalKey((k) => k + 1); setModalOpen(true) }
  const openEdit = (s: Service, tab: ModalTab = 'general') => { setEditingId(s.id); setModalInitialTab(tab); setModalKey((k) => k + 1); setModalOpen(true) }
  const handleModalOpen = (open: boolean) => { if (!open) setEditingId(null); setModalOpen(open) }
  const toggleGroup = (groupName: string) => setCollapsedGroups((prev) => ({ ...prev, [groupName]: !prev[groupName] }))

  const renderCard = (s: Service, drag?: CardDrag) => {
    const hasHealthCheckUrl = Boolean(s.healthCheckUrl?.trim())
    const hasAdditionalUrl = Boolean(s.additionalUrl?.trim())
    const dockerStatusLabel = s.dockerUpdateStatus != null
      ? resolveDockerUpdateStatus(s.dockerUpdateStatus)
      : null
    const hasUpdateAvailable = dockerStatusLabel === 'UpdateAvailable'
    const proxmoxStatusLabel = s.proxmoxUpdateStatus != null
      ? resolveDockerUpdateStatus(s.proxmoxUpdateStatus)
      : null
    const hasProxmoxUpdate = proxmoxStatusLabel === 'UpdateAvailable'

    // In Custom sort the card stays fully clickable; dragging is initiated only from the
    // grip handle (so touch scrolling over the card body still works). data-sort-id marks
    // the card as a drop target; wasDragged() swallows the click after a same-card drop.
    return (
      <div
        key={s.id}
        className={cn('service-card', 'service-card-clickable', drag?.dragging && 'service-card-dragging')}
        role="button"
        tabIndex={0}
        {...(drag ? { 'data-sort-id': drag.id } : {})}
        onClick={() => { if (drag?.wasDragged()) return; openEdit(s, 'general') }}
        onContextMenu={(e) => {
          e.preventDefault()
          setCardMenu({ service: s, x: e.clientX, y: e.clientY })
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openEdit(s, 'general') }
        }}
      >
        {/* Header row: drag grip (custom sort) | initial badge | name | healthcheck dot | actions menu */}
        <div className="flex items-center gap-2">
          {drag && (
            <GripVertical
              className="service-card-grip h-4 w-4"
              aria-label="Drag to reorder"
              onPointerDown={drag.onPointerDown}
              onClick={(e) => e.stopPropagation()}
            />
          )}
          <span
            className="service-card-logo"
            style={{
              backgroundImage: s.customLogoPath ?? s.faviconUrl
                ? `url(${s.customLogoPath ?? s.faviconUrl})`
                : undefined,
              backgroundSize: 'cover',
              backgroundPosition: 'center',
            }}
          >
            {!(s.customLogoPath ?? s.faviconUrl) && initials(s.name)}
          </span>
          <span
            className="service-card-name"
          >
            {s.name}
          </span>
          {hasHealthCheckUrl && (
            <span
              className={cn('h-2 w-2 shrink-0 rounded-full', statusDotClass(s.currentStatus))}
              title={statusTitle(s)}
            />
          )}
          {hasUpdateAvailable && (
            <span
              className="service-card-update-badge"
              title="A newer image is published in the registry. Open the service for details."
            >
              Update
            </span>
          )}
          {hasProxmoxUpdate && (
            <span
              className="service-card-update-badge service-card-update-badge-proxmox"
              title="A linked Proxmox guest (LXC/VM) has package updates pending. Open the service for details."
            >
              PVE
            </span>
          )}
          <button
            type="button"
            className="service-card-menu-trigger"
            aria-label={`Actions for ${s.name}`}
            onClick={(e) => {
              e.stopPropagation()
              const r = e.currentTarget.getBoundingClientRect()
              setCardMenu((m) => m?.service.id === s.id ? null : { service: s, x: r.left, y: r.bottom + 4 })
            }}
          >
            <MoreVertical className="h-3.5 w-3.5" />
          </button>
          {cardMenu?.service.id === s.id && (
            <FloatingMenu pos={{ x: cardMenu.x, y: cardMenu.y }} onClose={() => setCardMenu(null)}>
              {SERVICE_TABS.map(([tab, label, Icon]) => (
                <button
                  key={tab}
                  className="cgroup-menu-item"
                  onClick={() => { setCardMenu(null); openEdit(s, tab) }}
                >
                  <Icon className="h-3.5 w-3.5" /> {label}
                </button>
              ))}
              <div className="cgroup-menu-sep" />
              <button
                className="cgroup-menu-item"
                disabled={checkNow.isPending}
                onClick={() => { setCardMenu(null); checkNow.mutate(s.id) }}
              >
                <RefreshCw className={cn('h-3.5 w-3.5', checkNow.isPending && 'animate-spin')} /> Check now
              </button>
            </FloatingMenu>
          )}
        </div>

        {/* URL rows - flex-1 pushes footer to bottom */}
        <div className="service-card-urls">
          {/* Main URL */}
          <div className="service-card-url-row">
            {s.mainUrlHealthCheckEnabled ? (
              <span
                className={cn('h-2 w-2 shrink-0 rounded-full', statusDotClass(s.currentStatus))}
                title={statusTitle(s)}
              />
            ) : (
              <span className="h-2 w-2 shrink-0" />
            )}
            <a
              href={s.mainUrl}
              target="_blank"
              rel="noreferrer"
              className="service-card-url-link"
              onClick={(e) => e.stopPropagation()}
            >
              {s.mainUrl.replace(/^https?:\/\//, '')}
            </a>
          </div>

          {/* Additional URL */}
          {hasAdditionalUrl && (
            <div className="service-card-url-row">
              {s.additionalUrlHealthCheckEnabled ? (
                <span
                  className={cn('h-2 w-2 shrink-0 rounded-full', statusDotClass(s.additionalUrlStatus))}
                  title={`Additional: ${resolveStatus(s.additionalUrlStatus)}`}
                />
              ) : (
                <span className="h-2 w-2 shrink-0" />
              )}
              <a
                href={s.additionalUrl!}
                target="_blank"
                rel="noreferrer"
                className="service-card-url-link"
                onClick={(e) => e.stopPropagation()}
              >
                {s.additionalUrl!.replace(/^https?:\/\//, '')}
              </a>
            </div>
          )}
        </div>


        {/* Footer: category badge + tags (optional) */}
        <div className="h-px bg-border" />
          {/* Quick-access tab icons — open the modal straight to a section, mirroring
              the icon rows on the Docker container & Proxmox guest cards. */}
          <div className="service-card-tabs" role="group" aria-label={`Open a section of ${s.name}`}>
            {SERVICE_TABS.map(([tab, label, Icon]) => (
              <button
                key={tab}
                type="button"
                className="service-card-tab-btn"
                title={label}
                aria-label={label}
                onClick={(e) => { e.stopPropagation(); openEdit(s, tab) }}
              >
                <Icon className="h-3.5 w-3.5" />
              </button>
            ))}
          </div>
          {(s.categoryName || s.tags.length > 0) && (
            <div className="service-card-footer">
              {s.categoryName && (
                <span
                  className="service-card-category"
                  style={{ backgroundColor: s.categoryColor ?? 'var(--primary)' }}
                >
                  {s.categoryName}
                </span>
              )}
              {s.tags.map((t) => (
                <span key={t} className="service-card-tag">{t}</span>
              ))}
            </div>
          )}
      </div>
    )
  }

  useEffect(() => {
    let cancelled = false
    accountApi.getDashboardPreferences()
      .then((preferences) => {
        if (cancelled) return
        setSortMode(preferences.sortMode)
        setGroupByCategory(preferences.groupByCategory)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [])

  const saveDashboardPreferences = (nextSortMode: DashboardSortMode, nextGroupByCategory: boolean) => {
    accountApi.updateDashboardPreferences(nextSortMode, nextGroupByCategory)
      .catch(() => undefined)
  }

  return (
    <>
      <div className="dashboard-header">
        <div className="dashboard-header-row">
          <h1 className="text-2xl font-semibold">Services</h1>
          <Button onClick={openNew}><Plus className="h-3.5 w-3.5" /> Add service</Button>
        </div>
        <div className="dashboard-controls">
          <div className="dashboard-search">
            <Search className="dashboard-search-icon" />
            <Input className="dashboard-search-input" placeholder="Search…" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
          <select
            className="dashboard-select"
            value={categoryFilter}
            onChange={(e) => setCategoryFilter(e.target.value)}
          >
            <option value="">All categories</option>
            {categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
          <div className="dashboard-settings">
            <select
              className="dashboard-select"
              value={sortMode}
              onChange={(e) => {
                const nextSortMode = e.target.value as DashboardSortMode
                setSortMode(nextSortMode)
                saveDashboardPreferences(nextSortMode, groupByCategory)
              }}
            >
              <option value="name">Sort by name</option>
              <option value="category">Sort by category</option>
              <option value="custom">Custom order</option>
            </select>
            <label className="dashboard-toggle">
              <input
                type="checkbox"
                checked={groupByCategory}
                onChange={(e) => {
                  const nextGroupByCategory = e.target.checked
                  setGroupByCategory(nextGroupByCategory)
                  saveDashboardPreferences(sortMode, nextGroupByCategory)
                }}
              />
              Group by category
            </label>
          </div>
        </div>
      </div>

      {isLoading ? (
        <p className="text-muted-foreground">Loading…</p>
      ) : filtered.length === 0 ? (
        <p className="dashboard-empty">
          No services yet. Click <strong>+ Add service</strong> to start.
        </p>
      ) : groupByCategory ? (
        <div className="dashboard-groups">
          {(isCustom
            ? [...grouped].sort((a, b) => {
                if (a.categoryId === null) return 1
                if (b.categoryId === null) return -1
                return groupSort.order.indexOf(a.categoryId) - groupSort.order.indexOf(b.categoryId)
              })
            : grouped
          ).map((group) => (
            <SortableCardGroup
              key={group.name}
              group={group}
              isCustom={isCustom}
              collapsed={collapsedGroups[group.name] ?? false}
              onToggle={() => toggleGroup(group.name)}
              onCommitCards={commitCardsInCategory}
              groupSort={isCustom ? groupSort : null}
              renderCard={renderCard}
            />
          ))}
        </div>
      ) : (
        <div className="dashboard-grid">
          {(isCustom
            ? serviceSort.order
                .map((id) => filtered.find((s) => s.id === id))
                .filter((s): s is Service => Boolean(s))
            : filtered
          ).map((s) =>
            isCustom
              ? renderCard(s, { id: s.id, dragging: serviceSort.draggingId === s.id, onPointerDown: (e) => serviceSort.start(s.id, e), wasDragged: serviceSort.wasDragged })
              : renderCard(s),
          )}
        </div>
      )}

      <ServiceModal key={modalKey} open={modalOpen} onOpenChange={handleModalOpen} service={editingService} initialTab={modalInitialTab} />
    </>
  )
}

/** V10.6 — one dashboard category group. Extracted into its own component so each
 *  group can own a pointer-sort controller for its cards (hooks can't run in a loop).
 *  The group header is a drag handle for the group order (Custom mode); the cards
 *  reorder within the group. Cross-group card drags are ignored because each group's
 *  controller only knows its own ids. */
function SortableCardGroup({
  group, isCustom, collapsed, onToggle, onCommitCards, groupSort, renderCard,
}: {
  group: { name: string; categoryId: string | null; services: Service[] }
  isCustom: boolean
  collapsed: boolean
  onToggle: () => void
  onCommitCards: (categoryId: string | null, ids: string[]) => void
  groupSort: PointerSort | null
  renderCard: (s: Service, drag?: CardDrag) => ReactNode
}) {
  const cardSort = usePointerSort(
    group.services.map((s) => s.id),
    (ids) => onCommitCards(group.categoryId, ids),
  )

  const byId = new Map(group.services.map((s) => [s.id, s]))
  const ordered = isCustom
    ? cardSort.order.map((id) => byId.get(id)).filter((s): s is Service => Boolean(s))
    : group.services

  // Only real categories (with an id) reorder; "Uncategorized" is pinned last.
  const headerDraggable = isCustom && group.categoryId !== null && groupSort !== null
  const headerDragging = headerDraggable && groupSort!.draggingId === group.categoryId

  return (
    <section className="dashboard-group">
      <button
        type="button"
        className={cn('dashboard-group-button', headerDragging && 'dashboard-group-dragging')}
        onClick={() => { if (headerDraggable && groupSort!.wasDragged()) return; onToggle() }}
        {...(headerDraggable ? { 'data-group-sort-id': group.categoryId! } : {})}
      >
        <span className="dashboard-group-title">
          {headerDraggable && (
            <GripVertical
              className="h-4 w-4 dashboard-group-grip"
              onPointerDown={(e) => { e.stopPropagation(); groupSort!.start(group.categoryId!, e) }}
            />
          )}
          {collapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          {group.name}
        </span>
        <span className="dashboard-group-count">{group.services.length}</span>
      </button>
      {!collapsed && (
        <div className="dashboard-grid">
          {ordered.map((s) =>
            isCustom
              ? renderCard(s, { id: s.id, dragging: cardSort.draggingId === s.id, onPointerDown: (e) => cardSort.start(s.id, e), wasDragged: cardSort.wasDragged })
              : renderCard(s),
          )}
        </div>
      )}
    </section>
  )
}
