import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Activity, ChevronDown, ChevronRight, Container, Info, KeyRound, MoreVertical, Plus, RefreshCw, Search, Server } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useCategories, useCheckNow, useServices } from '@/lib/queries'
import { ServiceModal, type ModalTab } from '@/components/ServiceModal'
import { FloatingMenu } from '@/components/shared/FloatingMenu'
import { resolveDockerUpdateStatus, type Service, type ServiceStatus } from '@/lib/types'
import { accountApi } from '@/lib/account-api'
import { cn } from '@/lib/utils'
import '@/styles/dashboard.css'

/** Tabs surfaced in the card's "⋮" menu, mirroring the order + labels of the
 *  ServiceModal tab strip so the menu and the modal stay in lockstep. */
const SERVICE_TABS: ReadonlyArray<[ModalTab, string, typeof Info]> = [
  ['general', 'General', Info],
  ['healthcheck', 'Healthcheck', Activity],
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
  const [sortMode, setSortMode] = useState<'name' | 'category'>('name')
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

  const filtered = useMemo(() => {
    const loweredSearch = search.toLowerCase()
    return services
      .filter((s) => {
        if (loweredSearch && !s.name.toLowerCase().includes(loweredSearch) && !s.mainUrl.toLowerCase().includes(loweredSearch) && !(s.additionalUrl?.toLowerCase().includes(loweredSearch))) return false
        if (categoryFilter && s.categoryId !== categoryFilter) return false
        return true
      })
      .sort((left, right) => {
        if (sortMode === 'category') {
          const categoryCompare = normalizeCategoryName(left).localeCompare(normalizeCategoryName(right))
          if (categoryCompare !== 0) return categoryCompare
        }
        return left.name.localeCompare(right.name)
      })
  }, [services, search, categoryFilter, sortMode])

  const grouped = useMemo(() => {
    const groups = new Map<string, Service[]>()
    for (const service of filtered) {
      const key = normalizeCategoryName(service)
      const items = groups.get(key) ?? []
      items.push(service)
      groups.set(key, items)
    }
    return [...groups.entries()]
  }, [filtered])

  const openNew = () => { setEditingId(null); setModalInitialTab('general'); setModalKey((k) => k + 1); setModalOpen(true) }
  const openEdit = (s: Service, tab: ModalTab = 'general') => { setEditingId(s.id); setModalInitialTab(tab); setModalKey((k) => k + 1); setModalOpen(true) }
  const handleModalOpen = (open: boolean) => { if (!open) setEditingId(null); setModalOpen(open) }
  const toggleGroup = (groupName: string) => setCollapsedGroups((prev) => ({ ...prev, [groupName]: !prev[groupName] }))

  const renderCard = (s: Service) => {
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

    return (
      <div
        key={s.id}
        className="service-card service-card-clickable"
        role="button"
        tabIndex={0}
        onClick={() => openEdit(s, 'general')}
        onContextMenu={(e) => {
          e.preventDefault()
          setCardMenu({ service: s, x: e.clientX, y: e.clientY })
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openEdit(s, 'general') }
        }}
      >
        {/* Header row: initial badge | name | optional healthcheck dot | actions menu */}
        <div className="flex items-center gap-2">
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

        {/* Footer: category badge + tags */}
        {(s.categoryName || s.tags.length > 0) && (
          <>
            <div className="h-px bg-border" />
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
          </>
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

  const saveDashboardPreferences = (nextSortMode: 'name' | 'category', nextGroupByCategory: boolean) => {
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
                const nextSortMode = e.target.value as 'name' | 'category'
                setSortMode(nextSortMode)
                saveDashboardPreferences(nextSortMode, groupByCategory)
              }}
            >
              <option value="name">Sort by name</option>
              <option value="category">Sort by category</option>
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
          {grouped.map(([groupName, groupServices]) => {
            const isCollapsed = collapsedGroups[groupName] ?? false
            return (
              <section key={groupName} className="dashboard-group">
                <button
                  type="button"
                  className="dashboard-group-button"
                  onClick={() => toggleGroup(groupName)}
                >
                  <span className="dashboard-group-title">
                    {isCollapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                    {groupName}
                  </span>
                  <span className="dashboard-group-count">{groupServices.length}</span>
                </button>
                {!isCollapsed && (
                  <div className="dashboard-grid">
                    {groupServices.map(renderCard)}
                  </div>
                )}
              </section>
            )
          })}
        </div>
      ) : (
        <div className="dashboard-grid">
          {filtered.map(renderCard)}
        </div>
      )}

      <ServiceModal key={modalKey} open={modalOpen} onOpenChange={handleModalOpen} service={editingService} initialTab={modalInitialTab} />
    </>
  )
}
