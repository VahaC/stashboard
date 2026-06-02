import { useEffect, useState } from 'react'
import { Activity } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { settingsApi } from '@/lib/settings-api'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V5.6 — Settings → Health checks. App-wide tuning for the offline-alert
 * reliability logic introduced in V5.3.2: how many in-probe retries to make on
 * a transient connection failure, how long to wait between them, and how many
 * consecutive failed scans must pile up before a service is marked Down and an
 * offline notification fires. Higher values trade alert latency for fewer
 * false alarms; lower values alert faster but are more sensitive to blips.
 */
export function HealthCheckSettings() {
  const [intervalSeconds, setIntervalSeconds] = useState(60)
  const [failureThreshold, setFailureThreshold] = useState(3)
  const [retryCount, setRetryCount] = useState(2)
  const [retryDelayMs, setRetryDelayMs] = useState(1000)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getHealthCheckSettings()
      .then((s) => {
        setIntervalSeconds(s.intervalSeconds)
        setFailureThreshold(s.failureThreshold)
        setRetryCount(s.retryCount)
        setRetryDelayMs(s.retryDelayMs)
      })
      .catch(() => { /* leave defaults */ })
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      const interval = Number.isFinite(intervalSeconds) && intervalSeconds >= 10 ? Math.floor(intervalSeconds) : 10
      const threshold = Number.isFinite(failureThreshold) && failureThreshold > 0 ? Math.floor(failureThreshold) : 1
      const retries = Number.isFinite(retryCount) && retryCount >= 0 ? Math.floor(retryCount) : 0
      const delay = Number.isFinite(retryDelayMs) && retryDelayMs >= 0 ? Math.floor(retryDelayMs) : 0
      await settingsApi.updateHealthCheckSettings({
        intervalSeconds: interval,
        failureThreshold: threshold,
        retryCount: retries,
        retryDelayMs: delay,
      })
      setMessage({ kind: 'ok', text: 'Health-check settings saved. They apply on the next scan.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save health-check settings.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Health checks</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Activity className="h-5 w-5" /> Offline-alert reliability
          </CardTitle>
          <CardDescription>
            Stashboard probes every monitored service on a fixed schedule — by
            default once a minute. A single momentary network blip on the
            monitoring host — a DNS miss, a timeout, a TLS hiccup — should not
            page you. These settings control how often that probe runs and how
            forgiving it is before it declares a service offline and sends a{' '}
            <strong>🔴 Service unavailable</strong> notification.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} className="account-form account-form-spaced">
            <div className="account-form-row">
              <label htmlFor="hc-interval" className="account-form-label">
                Check interval (seconds)
              </label>
              <Input
                id="hc-interval"
                type="number"
                min={10}
                max={86400}
                value={Number.isFinite(intervalSeconds) ? intervalSeconds : ''}
                disabled={!loaded}
                onChange={(event) => {
                  const parsed = parseInt(event.target.value, 10)
                  setIntervalSeconds(Number.isFinite(parsed) ? parsed : 0)
                }}
                className="account-form-input"
              />
              <p className="text-xs text-[var(--muted-foreground)]">
                How often Stashboard probes <strong>every</strong> monitored
                service. Default <strong>60</strong> seconds (once a minute).
                Minimum 10 s. Lower values detect outages sooner but make more
                requests; higher values are gentler on the network and the
                targets. With retries and the failure threshold below, the worst
                case time to a confirmed alert is roughly{' '}
                <em>interval × failure threshold</em>.
              </p>
            </div>

            <div className="account-form-row">
              <label htmlFor="hc-failure-threshold" className="account-form-label">
                Failure threshold (consecutive scans)
              </label>
              <Input
                id="hc-failure-threshold"
                type="number"
                min={1}
                max={100}
                value={Number.isFinite(failureThreshold) ? failureThreshold : ''}
                disabled={!loaded}
                onChange={(event) => {
                  const parsed = parseInt(event.target.value, 10)
                  setFailureThreshold(Number.isFinite(parsed) ? parsed : 0)
                }}
                className="account-form-input"
              />
              <p className="text-xs text-[var(--muted-foreground)]">
                How many <strong>consecutive failed scans</strong> are required
                before a service flips to <strong>Down</strong> and an offline
                alert fires. A single success resets the counter. Default{' '}
                <strong>3</strong>. Set to <strong>1</strong> to alert on the
                very first failure.
              </p>
            </div>

            <div className="account-form-row">
              <label htmlFor="hc-retry-count" className="account-form-label">
                In-probe retries
              </label>
              <Input
                id="hc-retry-count"
                type="number"
                min={0}
                max={20}
                value={Number.isFinite(retryCount) ? retryCount : ''}
                disabled={!loaded}
                onChange={(event) => {
                  const parsed = parseInt(event.target.value, 10)
                  setRetryCount(Number.isFinite(parsed) ? parsed : 0)
                }}
                className="account-form-input"
              />
              <p className="text-xs text-[var(--muted-foreground)]">
                Extra attempts <strong>within a single probe</strong> when the
                request fails for a connection-level reason (DNS, timeout,
                network unreachable, TLS handshake). A real HTTP response —
                including a 5xx — is <strong>never</strong> retried, so genuine
                outages are still caught immediately. Total attempts = retries +
                1. Default <strong>2</strong>. Set to <strong>0</strong> to
                disable retries.
              </p>
            </div>

            <div className="account-form-row">
              <label htmlFor="hc-retry-delay" className="account-form-label">
                Retry delay (milliseconds)
              </label>
              <Input
                id="hc-retry-delay"
                type="number"
                min={0}
                max={60000}
                value={Number.isFinite(retryDelayMs) ? retryDelayMs : ''}
                disabled={!loaded}
                onChange={(event) => {
                  const parsed = parseInt(event.target.value, 10)
                  setRetryDelayMs(Number.isFinite(parsed) ? parsed : 0)
                }}
                className="account-form-input"
              />
              <p className="text-xs text-[var(--muted-foreground)]">
                How long to wait <strong>between in-probe retries</strong>.
                Default <strong>1000</strong> ms. Ignored when retries are 0.
              </p>
            </div>

            {message && (
              <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>
            )}

            <Button type="submit" disabled={saving || !loaded}>
              {saving ? 'Saving…' : 'Save'}
            </Button>
          </form>

          <section className="host-shell-settings-section">
            <h3>How they work together</h3>
            <ul className="host-shell-settings-conditions">
              <li>
                <strong>Retries absorb instant blips.</strong> A flaky probe is
                retried up to <em>In-probe retries</em> times (waiting{' '}
                <em>Retry delay</em> between attempts) before it counts as a
                failed scan at all.
              </li>
              <li>
                <strong>The threshold absorbs short outages.</strong> Even after
                a scan fails, the service is only marked Down once{' '}
                <em>Failure threshold</em> scans in a row have failed — so a
                brief outage that recovers within a scan or two never alerts.
              </li>
              <li>
                <strong>Genuine outages still fire.</strong> An HTTP error
                response is never retried, and once the threshold is crossed the
                alert fires on the next scan.
              </li>
              <li>
                <strong>Applied on the next scan.</strong> Changes here take
                effect on the following scheduled probe sweep — no restart
                needed. These replace the <code>STASHBOARD_HealthCheck__*</code>{' '}
                env vars, which now only seed the defaults on first run.
              </li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
