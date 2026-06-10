import { useEffect, useState } from 'react'
import { HardDrive } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { settingsApi } from '@/lib/settings-api'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V5.5 — Settings → Image cleanup. App-wide toggle + interval for the
 * background image-prune sweep. Per-connection opt-in lives on the
 * connection form on the Docker page; this page only controls the
 * global gate and cadence.
 */
export function ImageCleanupSettings() {
  const [enabled, setEnabled] = useState(true)
  const [intervalHours, setIntervalHours] = useState(168)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getImagePruneSettings()
      .then((s) => { setEnabled(s.enabled); setIntervalHours(s.intervalHours) })
      .catch(() => { /* leave defaults */ })
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      const hours = Number.isFinite(intervalHours) && intervalHours > 0 ? Math.floor(intervalHours) : 1
      await settingsApi.updateImagePruneSettings({ enabled, intervalHours: hours })
      setMessage({ kind: 'ok', text: 'Image-cleanup settings saved.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save image-cleanup settings.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Image cleanup (Docker)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <HardDrive className="h-5 w-5" /> Scheduled image prune
          </CardTitle>
          <CardDescription>
            Auto-updates leave the previous container image tagged
            <code> &lt;none&gt;:&lt;none&gt;</code> once the new image is in use.
            Over months these dangling images can grow to many gigabytes. When
            enabled, Stashboard runs <code>docker image prune</code> on each
            connection that opts in, on the cadence below, and records the
            outcome (images deleted, bytes freed) on the Storage widget.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} className="account-form account-form-spaced">
            <label className="account-checkbox-label">
              <input
                type="checkbox"
                checked={enabled}
                disabled={!loaded}
                onChange={(event) => setEnabled(event.target.checked)}
              />
              Enable the scheduled image prune
            </label>

            <div className="account-form-row">
              <label htmlFor="image-prune-interval" className="account-form-label">
                Interval (hours)
              </label>
              <Input
                id="image-prune-interval"
                type="number"
                min={1}
                max={8760}
                value={Number.isFinite(intervalHours) ? intervalHours : ''}
                disabled={!loaded}
                onChange={(event) => {
                  const parsed = parseInt(event.target.value, 10)
                  setIntervalHours(Number.isFinite(parsed) ? parsed : 0)
                }}
                className="account-form-input"
              />
              <p className="text-xs text-[var(--muted-foreground)]">
                Default <strong>168</strong> (weekly). Minimum 1 hour.
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
            <h3>Scope &amp; safety</h3>
            <ul className="host-shell-settings-conditions">
              <li>
                <strong>Dangling only by default.</strong> The scheduled sweep removes images whose only tag is
                <code> &lt;none&gt;:&lt;none&gt;</code> — Docker calls these "dangling". They are the orphaned
                previous-version images recreate leaves behind, and are always safe to delete.
              </li>
              <li>
                <strong>Per-connection opt-in.</strong> Each Docker connection has its own toggle on the connection
                form. Newly added connections opt in by default; turn it off there to skip a host.
              </li>
              <li>
                <strong>Opt-in "unused" scope.</strong> A connection can be set to also prune images not referenced
                by any container — more aggressive, and can break a rollback-to-previous-tag workflow because the
                previous image is removed too. Off by default.
              </li>
              <li>
                <strong>Manual override.</strong> The "Prune now" button on the Storage widget on the Docker page
                always works regardless of the toggle here. The dialog shows a dry-run preview before committing.
              </li>
              <li>
                <strong>Never touches volumes.</strong> Volume cleanup is intentionally out of scope.
              </li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
