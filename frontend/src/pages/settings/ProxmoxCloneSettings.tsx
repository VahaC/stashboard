import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Copy } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V8.0 — Settings → Clone/snapshot LXC. The app-wide master switch for cloning a
 * guest (`POST …/lxc/{vmid}/clone`) and managing snapshots (take / roll back /
 * delete). Like the destroy / create pages, it leads with what the actions do and
 * spells out every condition required before the affordances appear.
 */
export function ProxmoxCloneSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxCloneSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxCloneSettings({ enabled })
      // The Clone affordance + Snapshots tab gate on the cached feature flag —
      // refresh it so the change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Clone/snapshot enabled server-wide.' : 'Clone/snapshot disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save clone/snapshot setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Clone/snapshot LXC (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Copy className="h-5 w-5" /> Clone &amp; snapshot LXC
          </CardTitle>
          <CardDescription>
            Adds a <strong>Clone</strong> button and a <strong>Snapshots</strong> tab to a Proxmox container — stamp out a
            copy of a known-good container (<code>POST …/lxc/&#123;vmid&#125;/clone</code>) or take a point-in-time snapshot you
            can roll back to before a risky change, without leaving Stashboard.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This writes real state on the host.</strong> A clone consumes disk on the chosen storage; a rollback
              <em> discards</em> any state newer than the snapshot. It is off by default — leave it off unless you want
              operators to be able to clone containers and manage snapshots from the dashboard.
            </div>
          </div>

          <form onSubmit={submit} className="account-form account-form-spaced">
            <label className="account-checkbox-label">
              <input
                type="checkbox"
                checked={enabled}
                disabled={!loaded}
                onChange={(event) => setEnabled(event.target.checked)}
              />
              Enable clone/snapshot server-wide
            </label>

            {message && (
              <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>
            )}

            <Button type="submit" disabled={saving || !loaded}>
              {saving ? 'Saving…' : 'Save'}
            </Button>
          </form>

          <section className="host-shell-settings-section">
            <h3>What turning this on does — and doesn't — allow</h3>
            <p>
              This switch is only the <em>global gate</em>. Even with it on, the Clone button + Snapshots tab appear for a
              host only when <strong>both</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li><strong>This setting is on</strong> (server-wide master switch — what you toggle above).</li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow clone/snapshot”</strong>. It's off by default and set per host.
              </li>
            </ol>
            <p>
              If either condition is missing, the clone / snapshot request is refused server-side — the gate isn't just a
              hidden button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Validated.</strong> The clone's VMID range is checked before the request leaves Stashboard; a VMID already in use is rejected.</li>
              <li><strong>Double-confirmed.</strong> Rolling back to or deleting a snapshot needs an explicit second confirmation — a rollback discards newer state.</li>
              <li><strong>Proxmox is authoritative.</strong> An impossible linked clone, a duplicate snapshot name, or any other host rejection is surfaced verbatim — nothing is silently swallowed.</li>
              <li><strong>Audited.</strong> Every clone / snapshot / rollback / delete records who triggered it, when, against which host / node / vmid, and the result — on the container's <strong>Audit</strong> tab.</li>
            </ul>
            <p className="host-shell-settings-note">
              Cross-node clone migration, scheduled snapshots, and snapshot trees beyond a flat list are out of scope.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
