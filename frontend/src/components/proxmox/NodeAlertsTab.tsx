import { useEffect, useRef, useState } from 'react'
import { AlertCircle, RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  useCheckProxmoxNodeAlerts,
  useProxmoxNodeAlerts,
  useProxmoxNodeAlertSettings,
  useUpdateProxmoxNodeAlertSettings,
} from '@/lib/proxmox-queries'
import type {
  ProxmoxConnection,
  ProxmoxNodeAlert,
  ProxmoxNodeAlertSettings,
  ProxmoxNodeAlertThresholdValues,
} from '@/lib/types'
import { cn, getApiErrorMessage } from '@/lib/utils'
import { useNowTick } from '@/lib/use-now-tick'
import { alertCategoryLabel, alertDetail, validateThresholds } from './nodeAlertsHelpers'

/**
 * V6.8.1 — the node modal's Alerts tab. Shows the node's currently-active alerts
 * (severity + metric + value + threshold + first-seen) and the enable/disable +
 * per-category + threshold-override controls. Reuses the Docker/LXC
 * `container-modal-*` / `service-modal-*` / `pve-*` surface so it's the same UI
 * as the rest of the modal, not a parallel one.
 *
 * Kept in its own module (rather than inline in NodeModal) so the alerting UI can
 * be unit-tested without pulling in the console tab's xterm dependency.
 */

const CATEGORY_FIELDS: ReadonlyArray<{ key: keyof ProxmoxNodeAlertSettings['categories']; label: string }> = [
  { key: 'cpu', label: 'CPU' },
  { key: 'memory', label: 'Memory' },
  { key: 'storage', label: 'Storage' },
  { key: 'thermal', label: 'Thermal' },
  { key: 'smart', label: 'SMART' },
  { key: 'network', label: 'Network' },
]

type ThresholdForm = ProxmoxNodeAlertThresholdValues
type CategoryForm = ProxmoxNodeAlertSettings['categories']

export function NodeAlertsTab({ connection }: { connection: ProxmoxConnection }) {
  const settingsQ = useProxmoxNodeAlertSettings(connection.id)
  const settings = settingsQ.data
  const enabled = settings?.enabled ?? false
  const alertsQ = useProxmoxNodeAlerts(connection.id, enabled)
  const update = useUpdateProxmoxNodeAlertSettings(connection.id)
  const check = useCheckProxmoxNodeAlerts(connection.id)

  // Editable copy of the categories + thresholds, initialised once from the
  // loaded settings so a background refetch doesn't clobber in-progress edits.
  const [cats, setCats] = useState<CategoryForm | null>(null)
  const [thr, setThr] = useState<ThresholdForm | null>(null)
  const initialised = useRef(false)
  const [validationError, setValidationError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  useEffect(() => {
    if (settings && !initialised.current) {
      setCats(settings.categories)
      setThr(settings.thresholds)
      initialised.current = true
    }
  }, [settings])

  if (settingsQ.isLoading || !settings || !cats || !thr) {
    return <p className="container-modal-empty">Loading alert settings…</p>
  }
  if (settingsQ.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(settingsQ.error) ?? 'Failed to read alert settings'}
      </p>
    )
  }

  const toggleEnabled = (next: boolean) => {
    setActionError(null)
    update.mutate(
      { enabled: next, categories: cats, thresholds: thr },
      { onError: (e) => setActionError(getApiErrorMessage(e) ?? 'Failed to update alerting') },
    )
  }

  const save = () => {
    const err = validateThresholds(thr)
    if (err) { setValidationError(err); return }
    setValidationError(null)
    setActionError(null)
    update.mutate(
      { enabled: settings.enabled, categories: cats, thresholds: thr },
      { onError: (e) => setActionError(getApiErrorMessage(e) ?? 'Failed to save alert settings') },
    )
  }

  const runCheck = () => {
    setActionError(null)
    check.mutate(undefined, {
      onError: (e) => setActionError(getApiErrorMessage(e) ?? 'Failed to evaluate alerts'),
    })
  }

  const alerts = alertsQ.data ?? []

  return (
    <div className="container-modal-overview">
      <section className="container-modal-section">
        <div className="container-modal-stats-header pve-section-header">
          <h3 className="container-modal-section-title">Active alerts</h3>
          {enabled && (
            <Button type="button" size="sm" variant="outline" disabled={check.isPending} onClick={runCheck}>
              <RefreshCw className={cn('h-3.5 w-3.5', check.isPending && 'animate-spin')} /> Check now
            </Button>
          )}
        </div>
        {!enabled ? (
          <p className="container-modal-empty">
            Alerting is off for this node — enable it below to be notified when CPU, memory, storage, temperature,
            SMART health, or NIC errors cross their thresholds.
          </p>
        ) : alerts.length === 0 ? (
          <p className="container-modal-empty">
            No active alerts — every monitored metric is within its thresholds.
          </p>
        ) : (
          <div className="pve-rows">
            {alerts.map((a) => <AlertRow key={a.category} alert={a} />)}
          </div>
        )}
        {actionError && (
          <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {actionError}</p>
        )}
      </section>

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Monitoring</h3>
        {/* Docker-watch parity: the same checkbox + helper-text pattern as a
            Docker watch's "Enabled" toggle. Optimistic — flips immediately. */}
        <label className="service-modal-checkbox-label service-modal-label">
          <input
            type="checkbox"
            checked={enabled}
            disabled={update.isPending}
            onChange={(e) => toggleEnabled(e.target.checked)}
          />
          Node health alerting enabled
        </label>
        <p className="container-modal-empty">
          When on, this node is evaluated every few minutes and a notification is sent (email / Telegram, the same
          channels as update alerts) when a critical deviation persists. Off by default — the node analogue of
          enabling a Docker watch.
        </p>
      </section>

      {enabled && (
        <>
          <section className="container-modal-section">
            <h3 className="container-modal-section-title">Categories</h3>
            <p className="container-modal-empty">Choose which deviations may fire. All on by default.</p>
            <div className="pve-alert-categories">
              {CATEGORY_FIELDS.map(({ key, label }) => (
                <label key={key} className="service-modal-checkbox-label service-modal-label">
                  <input
                    type="checkbox"
                    checked={cats[key]}
                    onChange={(e) => setCats({ ...cats, [key]: e.target.checked })}
                  />
                  {label}
                </label>
              ))}
            </div>
          </section>

          <section className="container-modal-section">
            <h3 className="container-modal-section-title">Thresholds</h3>
            <p className="container-modal-empty">
              Leave a field blank to use the global default (shown as the placeholder). Tune a deliberately hot node
              without muting the fleet.
            </p>
            <div className="pve-alert-thresholds">
              <ThresholdRow label="CPU %" warnKey="cpuWarn" critKey="cpuCrit" values={thr} defaults={settings.defaults} onChange={setThr} />
              <ThresholdRow label="RAM %" warnKey="memWarn" critKey="memCrit" values={thr} defaults={settings.defaults} onChange={setThr} />
              <ThresholdRow label="Storage %" warnKey="storageWarn" critKey="storageCrit" values={thr} defaults={settings.defaults} onChange={setThr} />
              <ThresholdRow label="Temp °C" warnKey="tempWarn" critKey="tempCrit" values={thr} defaults={settings.defaults} onChange={setThr} />
            </div>
            {validationError && (
              <p className="container-modal-error"><AlertCircle className="h-3.5 w-3.5 inline" /> {validationError}</p>
            )}
            <div className="container-modal-actions">
              <Button type="button" size="sm" disabled={update.isPending} onClick={save}>
                Save thresholds
              </Button>
            </div>
            <p className="container-modal-empty">
              Thermal uses each chip's own high/critical limits where available, falling back to these. SMART
              (health ≠ PASSED / low wearout) and NIC error spikes use fixed sane defaults. A source that's merely
              unavailable (no SSH / lm-sensors) is treated as n/a and never alerts.
            </p>
          </section>
        </>
      )}
    </div>
  )
}

function AlertRow({ alert: a }: { alert: ProxmoxNodeAlert }) {
  const now = useNowTick()
  return (
    <div className="pve-row">
      <div className="pve-row-head">
        <span className="pve-row-name">{alertCategoryLabel(a.category)}{a.metric ? ` · ${a.metric}` : ''}</span>
        <span className="pve-health-badge" data-level={a.severity}>{a.severity === 'crit' ? 'critical' : 'warning'}</span>
      </div>
      <div className="pve-row-foot">
        {alertDetail(a)}
        {a.firstSeenUtc && <> · since {relativeTime(new Date(a.firstSeenUtc).getTime(), now)}</>}
      </div>
    </div>
  )
}

function ThresholdRow({ label, warnKey, critKey, values, defaults, onChange }: {
  label: string
  warnKey: keyof ThresholdForm
  critKey: keyof ThresholdForm
  values: ThresholdForm
  defaults: ProxmoxNodeAlertThresholdValues
  onChange: (next: ThresholdForm) => void
}) {
  const set = (key: keyof ThresholdForm, raw: string) => {
    const v = raw.trim() === '' ? null : Number(raw)
    onChange({ ...values, [key]: v != null && Number.isNaN(v) ? null : v })
  }
  return (
    <div className="pve-alert-threshold-row">
      <span className="pve-alert-threshold-label">{label}</span>
      <Input
        type="number" className="pve-alert-threshold-input"
        aria-label={`${label} warn`} placeholder={defaults[warnKey]?.toString() ?? ''}
        value={values[warnKey] ?? ''} onChange={(e) => set(warnKey, e.target.value)}
      />
      <span className="pve-alert-threshold-sep">warn /</span>
      <Input
        type="number" className="pve-alert-threshold-input"
        aria-label={`${label} crit`} placeholder={defaults[critKey]?.toString() ?? ''}
        value={values[critKey] ?? ''} onChange={(e) => set(critKey, e.target.value)}
      />
      <span className="pve-alert-threshold-sep">crit</span>
    </div>
  )
}

function relativeTime(epochMs: number, now: number): string {
  if (!epochMs) return 'never'
  const secs = Math.max(0, Math.round((now - epochMs) / 1000))
  if (secs < 2) return 'just now'
  if (secs < 60) return `${secs}s ago`
  const mins = Math.round(secs / 60)
  if (mins < 60) return `${mins} min ago`
  return `${Math.round(mins / 60)} h ago`
}
