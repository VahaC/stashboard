import { useMemo, useState } from 'react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import type { Service, ServiceStatus, ServiceUpsert } from '@/lib/types'
import { resolveDockerUpdateStatus } from '@/lib/types'
import { useCategories, useCheckNow, useDeleteService, useRefreshFavicon, useUploadLogo, useUpsertService } from '@/lib/queries'
import { Check, Copy, Eye, EyeOff, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { parseApiErrors } from '@/lib/utils'
import { cn } from '@/lib/utils'
import { DockerWatchSection } from '@/components/DockerWatchSection'
import '@/styles/service-modal.css'

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  service: Service | null
}

type ModalTab = 'general' | 'healthcheck' | 'credentials' | 'docker'

const empty: ServiceUpsert = {
  name: '',
  mainUrl: '',
  mainUrlHealthCheckEnabled: true,
  additionalUrl: null,
  additionalUrlHealthCheckEnabled: true,
  offlineNotificationsEnabled: true,
  healthCheckUrl: null,
  healthCheckMethod: 'Get',
  expectedStatusRange: null,
  notes: null,
  categoryId: null,
  logoSource: 'AutoFavicon',
  customLogoPath: null,
  tags: [],
  credentials: [],
  dockerConnectionId: null,
}

type CredRow = { uid: string; key: string; value: string; isSecret: boolean; reveal: boolean; copied: 'key' | 'value' | null }

const genUid = () =>
  (typeof crypto !== 'undefined' && crypto.randomUUID)
    ? crypto.randomUUID()
    : Math.random().toString(36).slice(2) + Date.now().toString(36)

const buildAutoIconPreview = (mainUrl: string) => {
  try {
    const url = new URL(mainUrl)
    return `https://www.google.com/s2/favicons?domain=${encodeURIComponent(url.hostname)}&sz=64`
  } catch {
    return null
  }
}

const resolveStatus = (s: ServiceStatus) =>
  typeof s === 'number' ? (['Unknown', 'Up', 'Down', 'NeedsAttention'][s] as string) : s

export function ServiceModal({ open, onOpenChange, service }: Props) {
  const { data: categories = [] } = useCategories()
  const upsert = useUpsertService()
  const del = useDeleteService()
  const checkNow = useCheckNow()
  const uploadLogo = useUploadLogo()
  const refreshFavicon = useRefreshFavicon()

  const [activeTab, setActiveTab] = useState<ModalTab>('general')
  const [form, setForm] = useState<ServiceUpsert>(() => service
    ? {
        name: service.name,
        mainUrl: service.mainUrl,
        mainUrlHealthCheckEnabled: service.mainUrlHealthCheckEnabled,
        additionalUrl: service.additionalUrl,
        additionalUrlHealthCheckEnabled: service.additionalUrlHealthCheckEnabled,
        offlineNotificationsEnabled: service.offlineNotificationsEnabled,
        healthCheckUrl: service.healthCheckUrl,
        healthCheckMethod: service.healthCheckMethod,
        expectedStatusRange: service.expectedStatusRange,
        notes: service.notes,
        categoryId: service.categoryId,
        logoSource: service.logoSource,
        customLogoPath: service.customLogoPath,
        tags: service.tags,
        credentials: [],
        dockerConnectionId: service.dockerConnectionId,
      }
    : empty
  )
  const [credRows, setCredRows] = useState<CredRow[]>(() =>
    service ? service.credentials.map((c) => ({ uid: genUid(), key: c.key, value: c.value, isSecret: c.isSecret, reveal: false, copied: null })) : []
  )

  const copyToClipboard = (uid: string, field: 'key' | 'value', text: string) => {
    navigator.clipboard.writeText(text).then(() => {
      setCredRows((rows) => rows.map((r) => r.uid === uid ? { ...r, copied: field } : r))
      setTimeout(() => setCredRows((rows) => rows.map((r) => r.uid === uid ? { ...r, copied: null } : r)), 1500)
    })
  }
  const [tagsInput, setTagsInput] = useState(() => service ? service.tags.join(', ') : '')
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [checkError, setCheckError] = useState<string | null>(null)
  const [faviconError, setFaviconError] = useState<string | null>(null)
  const [localLogoPreview, setLocalLogoPreview] = useState<string | null>(null)

  const iconPreview = useMemo(() => {
    if (form.logoSource === 'Custom') return localLogoPreview ?? form.customLogoPath ?? null
    return service?.faviconUrl ?? buildAutoIconPreview(form.mainUrl)
  }, [form.logoSource, form.customLogoPath, form.mainUrl, service?.faviconUrl, localLogoPreview])

  const hasAdditionalHealthCheckTarget = Boolean(form.additionalUrl?.trim())
  const hasAnyHealthCheckEnabled = form.mainUrlHealthCheckEnabled
    || (hasAdditionalHealthCheckTarget && form.additionalUrlHealthCheckEnabled)
  const shouldDisableOfflineNotifications = !hasAnyHealthCheckEnabled && !form.healthCheckUrl?.trim()

  // Tab indicators — let the user spot interesting state without clicking through.
  const healthcheckIndicator: ServiceStatus | null = useMemo(() => {
    if (!service) return null
    const main = resolveStatus(service.currentStatus)
    if (main === 'Down' || main === 'NeedsAttention') return service.currentStatus
    if (service.additionalUrl) {
      const add = resolveStatus(service.additionalUrlStatus)
      if (add === 'Down' || add === 'NeedsAttention') return service.additionalUrlStatus
    }
    return null
  }, [service])
  const dockerHasUpdate = service?.dockerUpdateStatus != null
    && resolveDockerUpdateStatus(service.dockerUpdateStatus) === 'UpdateAvailable'

  const save = async () => {
    setError(null)
    setFieldErrors({})
    const payload: ServiceUpsert = {
      ...form,
      offlineNotificationsEnabled: shouldDisableOfflineNotifications ? false : form.offlineNotificationsEnabled,
      tags: tagsInput.split(',').map((s) => s.trim()).filter(Boolean),
      credentials: credRows
        .filter((c) => c.key.trim().length > 0)
        .map((c) => ({ key: c.key, value: c.value, isSecret: c.isSecret })),
      // The Docker tab manages this field via its own upsert call and refreshes
      // the services cache. Reading it from `form` would send the stale
      // initial value and clobber a connection the user just assigned in this
      // same modal session.
      dockerConnectionId: service?.dockerConnectionId ?? null,
    }
    try {
      await upsert.mutateAsync({ id: service?.id, data: payload })
      onOpenChange(false)
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError)
    }
  }

  const onDelete = async () => {
    if (!service) return
    if (!confirm(`Delete "${service.name}"?`)) return
    await del.mutateAsync(service.id)
    onOpenChange(false)
  }

  const onLogoFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file || !service) return
    const objectUrl = URL.createObjectURL(file)
    setLocalLogoPreview(objectUrl)
    const path = await uploadLogo.mutateAsync({ id: service.id, file })
    setForm((f) => ({ ...f, customLogoPath: path, logoSource: 'Custom' }))
  }

  const addCredential = () => setCredRows((rows) => [
    ...rows, { uid: genUid(), key: '', value: '', isSecret: true, reveal: false, copied: null },
  ])

  const updateCredential = (uid: string, patch: Partial<{ key: string; value: string; isSecret: boolean; reveal: boolean }>) =>
    setCredRows((rows) => rows.map((c) => (c.uid === uid ? { ...c, ...patch } : c)))

  const removeCredential = (uid: string) =>
    setCredRows((rows) => rows.filter((c) => c.uid !== uid))

  const onRefreshFavicon = async () => {
    if (!service) return

    try {
      setFaviconError(null)
      const updatedService = await refreshFavicon.mutateAsync(service.id)
      setForm((currentForm) => ({
        ...currentForm,
        logoSource: updatedService.logoSource,
        customLogoPath: updatedService.customLogoPath,
      }))
    } catch {
      setFaviconError('Failed to refresh favicon')
    }
  }

  // status dot class helper (design-system tokens)
  const dotClass = (s: ServiceStatus) => {
    const v = resolveStatus(s)
    if (v === 'Up') return 'bg-[var(--status-up)]'
    if (v === 'Down') return 'bg-[var(--status-down)]'
    if (v === 'NeedsAttention') return 'bg-[var(--status-attention)]'
    return 'bg-[var(--status-unknown)]'
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="service-modal-content">

        {/* Header */}
        <DialogHeader className="service-modal-header">
          <DialogTitle className="service-modal-title">
            {service?.healthCheckUrl && (
              <span className={cn('h-2 w-2 shrink-0 rounded-full', dotClass(service.currentStatus))} />
            )}
            {service ? service.name : 'Add service'}
          </DialogTitle>
        </DialogHeader>

        {error && <p className="service-modal-error">{error}</p>}

        {/* Tab strip */}
        <div className="service-modal-tabs" role="tablist">
          <TabButton
            label="General"
            active={activeTab === 'general'}
            onClick={() => setActiveTab('general')}
          />
          <TabButton
            label="Healthcheck"
            active={activeTab === 'healthcheck'}
            onClick={() => setActiveTab('healthcheck')}
            indicatorClass={healthcheckIndicator != null ? dotClass(healthcheckIndicator) : null}
          />
          <TabButton
            label="Credentials"
            active={activeTab === 'credentials'}
            onClick={() => setActiveTab('credentials')}
            count={credRows.length || null}
          />
          <TabButton
            label="Docker"
            active={activeTab === 'docker'}
            onClick={() => setActiveTab('docker')}
            badge={dockerHasUpdate ? 'Update' : null}
            disabled={!service}
            disabledHint="Save the service first to configure Docker tracking."
          />
        </div>

        <div className="service-modal-tab-panel">

          {/* ── GENERAL TAB ───────────────────────────────────────────── */}
          {activeTab === 'general' && (
            <div className="service-modal-grid">
              <div className="service-modal-field">
                <Label className="service-modal-label">Name</Label>
                <Input
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  className={fieldErrors['name'] ? 'border-destructive' : ''}
                />
                {fieldErrors['name'] && <p className="service-modal-field-error">{fieldErrors['name']}</p>}
              </div>

              <div className="service-modal-field">
                <Label className="service-modal-label">Category</Label>
                <select
                  className="service-modal-select"
                  value={form.categoryId ?? ''}
                  onChange={(e) => setForm({ ...form, categoryId: e.target.value || null })}
                >
                  <option value="">(none)</option>
                  {categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">Main URL</Label>
                <Input
                  value={form.mainUrl}
                  onChange={(e) => setForm({ ...form, mainUrl: e.target.value })}
                  placeholder="https://service.example.com"
                  className={fieldErrors['mainUrl'] ? 'border-destructive' : ''}
                />
                {fieldErrors['mainUrl'] && <p className="service-modal-field-error">{fieldErrors['mainUrl']}</p>}
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">
                  Additional URL <span className="service-modal-label-help">(optional)</span>
                </Label>
                <Input
                  value={form.additionalUrl ?? ''}
                  onChange={(e) => setForm({ ...form, additionalUrl: e.target.value || null })}
                  placeholder="https://admin.service.example.com"
                  className={fieldErrors['additionalUrl'] ? 'border-destructive' : ''}
                />
                {fieldErrors['additionalUrl'] && <p className="service-modal-field-error">{fieldErrors['additionalUrl']}</p>}
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">
                  Tags <span className="service-modal-label-help">(comma-separated)</span>
                </Label>
                <Input
                  value={tagsInput}
                  onChange={(e) => setTagsInput(e.target.value)}
                  placeholder="prod, internal"
                  className={fieldErrors['tags'] ? 'border-destructive' : ''}
                />
                {fieldErrors['tags'] && <p className="service-modal-field-error">{fieldErrors['tags']}</p>}
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">Notes</Label>
                <Textarea
                  rows={3}
                  value={form.notes ?? ''}
                  onChange={(e) => setForm({ ...form, notes: e.target.value || null })}
                  className={fieldErrors['notes'] ? 'border-destructive' : ''}
                />
                {fieldErrors['notes'] && <p className="service-modal-field-error">{fieldErrors['notes']}</p>}
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">Logo</Label>
                <div className="service-modal-logo-row">
                  {iconPreview && (
                    <img
                      src={iconPreview}
                      alt=""
                      className="service-modal-logo-preview"
                      onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden' }}
                    />
                  )}
                  <div className="service-modal-logo-actions">
                    <select
                      className={cn('service-modal-select', 'w-auto')}
                      value={form.logoSource as string}
                      onChange={(e) => {
                        const next = e.target.value as 'AutoFavicon' | 'Custom'
                        if (next !== 'Custom') setLocalLogoPreview(null)
                        setForm({ ...form, logoSource: next })
                      }}
                    >
                      <option value="AutoFavicon">Auto (favicon)</option>
                      <option value="Custom">Custom upload</option>
                    </select>
                    {form.logoSource === 'Custom' && service && (
                      <label className="service-modal-upload">
                        Upload
                        <input type="file" accept="image/*" onChange={onLogoFile} className="sr-only" />
                      </label>
                    )}
                    {service && (
                      <Button type="button" variant="outline" size="sm" onClick={onRefreshFavicon} disabled={refreshFavicon.isPending}>
                        <RefreshCw className={cn('h-3.5 w-3.5', refreshFavicon.isPending && 'animate-spin')} />
                        Re-fetch favicon
                      </Button>
                    )}
                  </div>
                </div>
                {faviconError && <p className="service-modal-field-error">{faviconError}</p>}
              </div>
            </div>
          )}

          {/* ── HEALTHCHECK TAB ───────────────────────────────────────── */}
          {activeTab === 'healthcheck' && (
            <div className="service-modal-grid">
              {service && (
                <div className="service-modal-field service-modal-field-full">
                  <div className="service-modal-status-row">
                    <span className="service-modal-status-text">
                      {service.currentStatus != null
                        ? `${resolveStatus(service.currentStatus)} · ${service.lastResponseTimeMs != null ? `${service.lastResponseTimeMs} ms` : '—'}`
                        : 'Not checked yet'}
                    </span>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="shrink-0"
                      onClick={async () => { try { setCheckError(null); await checkNow.mutateAsync(service.id) } catch { setCheckError('Check failed') } }}
                      disabled={checkNow.isPending}
                    >
                      <RefreshCw className={cn('h-3.5 w-3.5', checkNow.isPending && 'animate-spin')} />
                      Check now
                    </Button>
                  </div>
                  {checkError && <p className="service-modal-field-error">{checkError}</p>}
                </div>
              )}

              <div className="service-modal-field service-modal-field-full">
                <label className="service-modal-checkbox-label service-modal-label">
                  <input
                    type="checkbox"
                    checked={form.mainUrlHealthCheckEnabled}
                    onChange={(e) => setForm({ ...form, mainUrlHealthCheckEnabled: e.target.checked })}
                  />
                  Healthcheck the main URL
                  {form.mainUrlHealthCheckEnabled && service && (
                    <span className={cn('ml-2 h-2 w-2 shrink-0 rounded-full', dotClass(service.currentStatus))} />
                  )}
                </label>
                <p className="service-modal-help">{form.mainUrl || '(set Main URL on the General tab)'}</p>
              </div>

              <div className="service-modal-field service-modal-field-full">
                <label className="service-modal-checkbox-label service-modal-label">
                  <input
                    type="checkbox"
                    checked={form.additionalUrlHealthCheckEnabled}
                    onChange={(e) => setForm({ ...form, additionalUrlHealthCheckEnabled: e.target.checked })}
                    disabled={!form.additionalUrl}
                  />
                  Healthcheck the additional URL
                  {form.additionalUrlHealthCheckEnabled && service?.additionalUrl && (
                    <span className={cn('ml-2 h-2 w-2 shrink-0 rounded-full', dotClass(service.additionalUrlStatus))} />
                  )}
                </label>
                <p className="service-modal-help">
                  {form.additionalUrl || '(no additional URL configured on General tab)'}
                </p>
              </div>

              <div className="service-modal-field service-modal-field-full">
                <Label className="service-modal-label">
                  Healthcheck URL <span className="service-modal-label-help">(optional · not shown on the card)</span>
                </Label>
                <Input
                  value={form.healthCheckUrl ?? ''}
                  onChange={(e) => setForm({ ...form, healthCheckUrl: e.target.value || null })}
                  placeholder="https://service.example.com/healthz"
                  className={fieldErrors['healthcheckurl'] ? 'border-destructive' : ''}
                />
                <p className="service-modal-help">
                  When set, this URL is probed instead of the main URL; status appears as a dot next to the service name.
                </p>
                {fieldErrors['healthcheckurl'] && <p className="service-modal-field-error">{fieldErrors['healthcheckurl']}</p>}
              </div>

              <div className="service-modal-field">
                <Label className="service-modal-label">HTTP method</Label>
                <select
                  className="service-modal-select"
                  value={form.healthCheckMethod as string}
                  onChange={(e) => setForm({ ...form, healthCheckMethod: e.target.value as 'Get' | 'Head' })}
                >
                  <option value="Get">GET</option>
                  <option value="Head">HEAD</option>
                </select>
              </div>

              <div className="service-modal-field">
                <Label className="service-modal-label">
                  Expected status <span className="service-modal-label-help">(e.g. 200-299)</span>
                </Label>
                <Input
                  placeholder="200-399"
                  value={form.expectedStatusRange ?? ''}
                  onChange={(e) => setForm({ ...form, expectedStatusRange: e.target.value || null })}
                  className={fieldErrors['expectedstatusrange'] ? 'border-destructive' : ''}
                />
                {fieldErrors['expectedstatusrange'] && <p className="service-modal-field-error">{fieldErrors['expectedstatusrange']}</p>}
              </div>

              <div className="service-modal-field service-modal-field-full">
                <label className="service-modal-checkbox-label service-modal-label">
                  <input
                    type="checkbox"
                    checked={shouldDisableOfflineNotifications ? false : form.offlineNotificationsEnabled}
                    onChange={(e) => setForm({ ...form, offlineNotificationsEnabled: e.target.checked })}
                    disabled={shouldDisableOfflineNotifications}
                  />
                  Notify when service goes offline
                </label>
                {shouldDisableOfflineNotifications && (
                  <p className="service-modal-notification-help">
                    Disabled — turn on at least one healthcheck above to enable offline notifications.
                  </p>
                )}
              </div>
            </div>
          )}

          {/* ── CREDENTIALS TAB ───────────────────────────────────────── */}
          {activeTab === 'credentials' && (
            <div className="service-modal-grid">
              <div className="service-modal-field service-modal-field-full gap-2">
                <div className="service-modal-credentials-header">
                  <Label className="service-modal-label">Credentials</Label>
                  <Button type="button" variant="outline" size="sm" onClick={addCredential}>
                    <Plus className="h-3.5 w-3.5" /> Add
                  </Button>
                </div>
                {credRows.length === 0
                  ? <p className="service-modal-credentials-empty">No credentials yet. Click <strong>Add</strong> to store a key/value pair (e.g. API token, dashboard login).</p>
                  : (
                    <div className="service-modal-credentials-list">
                      {credRows.map((c) => (
                        <div key={c.uid} className="service-modal-credentials-row">
                          <div className="relative">
                            <Input
                              placeholder="Key"
                              value={c.key}
                              onChange={(e) => updateCredential(c.uid, { key: e.target.value })}
                              className="font-mono text-[12px] pr-9"
                            />
                            <div className="service-modal-value-actions">
                              <button
                                type="button"
                                className="service-modal-copy-btn"
                                onClick={() => copyToClipboard(c.uid, 'key', c.key)}
                                title="Copy key"
                              >
                                {c.copied === 'key' ? <Check className="service-modal-copy-success h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                              </button>
                            </div>
                          </div>
                          <div className="relative">
                            <Input
                              type={c.isSecret && !c.reveal ? 'password' : 'text'}
                              placeholder="Value"
                              value={c.value}
                              onChange={(e) => updateCredential(c.uid, { value: e.target.value })}
                              className={cn('font-mono text-[12px]', c.isSecret ? 'pr-[4.5rem]' : 'pr-9')}
                            />
                            <div className="service-modal-value-actions">
                              <button
                                type="button"
                                className="service-modal-icon-btn"
                                onClick={() => copyToClipboard(c.uid, 'value', c.value)}
                                title="Copy value"
                              >
                                {c.copied === 'value' ? <Check className="service-modal-copy-success h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                              </button>
                              {c.isSecret && (
                                <button
                                  type="button"
                                  className="service-modal-icon-btn"
                                  onClick={() => updateCredential(c.uid, { reveal: !c.reveal })}
                                >
                                  {c.reveal ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
                                </button>
                              )}
                            </div>
                          </div>
                          <label className="service-modal-secret-toggle">
                            <input
                              type="checkbox"
                              checked={c.isSecret}
                              onChange={(e) => updateCredential(c.uid, { isSecret: e.target.checked, reveal: e.target.checked ? c.reveal : true })}
                            />
                            secret
                          </label>
                          <Button type="button" variant="ghost" size="icon" onClick={() => removeCredential(c.uid)}>
                            <Trash2 className="h-3.5 w-3.5" />
                          </Button>
                        </div>
                      ))}
                    </div>
                  )
                }
              </div>
            </div>
          )}

          {/* ── DOCKER TAB ────────────────────────────────────────────── */}
          {activeTab === 'docker' && (
            service
              ? <DockerWatchSection service={service} />
              : (
                <div className="service-modal-tab-placeholder">
                  Save the service first — Docker tracking is configured per existing service.
                </div>
              )
          )}

        </div>

        {/* Footer */}
        <div className="service-modal-footer">
          {service && (
            <Button variant="destructive" className="mr-auto" onClick={onDelete}>Delete</Button>
          )}
          <Button variant="secondary" onClick={() => onOpenChange(false)}>Close</Button>
          <Button onClick={save} disabled={upsert.isPending}>{upsert.isPending ? 'Saving…' : 'Save'}</Button>
        </div>

      </DialogContent>
    </Dialog>
  )
}

// ── Tab strip primitive (kept inline — only used here) ─────────────────────

interface TabButtonProps {
  label: string
  active: boolean
  onClick: () => void
  indicatorClass?: string | null
  count?: number | null
  badge?: string | null
  disabled?: boolean
  disabledHint?: string
}

function TabButton({ label, active, onClick, indicatorClass, count, badge, disabled, disabledHint }: TabButtonProps) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      data-active={active}
      disabled={disabled}
      title={disabled ? disabledHint : undefined}
      className="service-modal-tab"
      onClick={onClick}
    >
      {label}
      {count != null && count > 0 && (
        <span className="service-modal-label-help">({count})</span>
      )}
      {indicatorClass && (
        <span className={cn('service-modal-tab-dot', indicatorClass)} aria-hidden />
      )}
      {badge && (
        <span className="service-modal-tab-badge">{badge}</span>
      )}
    </button>
  )
}
