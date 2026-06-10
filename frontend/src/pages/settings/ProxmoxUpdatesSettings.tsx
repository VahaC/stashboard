import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, RefreshCw } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V6.7.1 — Settings → Proxmox updates. The app-wide master switch for one-click
 * "Update now" (applying pending package updates on the node and inside its LXCs
 * by SSHing to the host and running `apt-get dist-upgrade`). Like the LXC
 * console, the page leads with the risks and spells out every condition required
 * before the Update affordances appear.
 */
export function ProxmoxUpdatesSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxUpdatesSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxUpdatesSettings({ enabled })
      // The Update affordances gate on the cached feature flag — refresh it so
      // the change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Update now enabled server-wide.' : 'Update now disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save Proxmox-updates setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Proxmox updates (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <RefreshCw className="h-5 w-5" /> Proxmox "Update now"
          </CardTitle>
          <CardDescription>
            Adds a one-click <strong>Update now</strong> on the Proxmox page that <em>applies</em> pending package
            updates — the natural follow-up to the per-LXC update count. Stashboard SSHes to the Proxmox host and
            runs <code>apt-get update &amp;&amp; apt-get -y dist-upgrade</code>, either directly on the node or via
            <code> pct exec &lt;vmid&gt;</code> inside an LXC. Output streams live into the dialog.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This applies real package upgrades as root.</strong> An upgrade can restart services, and a
              node-level upgrade may pull a new kernel that only takes effect after a reboot. A check is a single
              SSH sweep, so a node-level Update now upgrades <strong>the whole node</strong>; an LXC-level one
              upgrades just that container. Leave it off unless you understand and accept that. It is off by default.
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
              Enable one-click Update now server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the Update affordances appear for a host
              only when <strong>all</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li><strong>This setting is on</strong> (server-wide master switch — what you toggle above).</li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow apply updates”</strong>. It's off by default and set per host.
              </li>
              <li>
                <strong>SSH is configured</strong> on the host (host / username / private key) — applying updates
                SSHes in and runs <code>apt-get</code>.
              </li>
            </ol>
            <p>
              If any condition is missing, the apply request is refused server-side — the gate isn't just a hidden
              button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Audited.</strong> Every run records who triggered it, when, against which host / node / guest, the exit status, how many bytes of output streamed, and how it ended — on the Audit page's <strong>Proxmox updates</strong> tab.</li>
              <li><strong>Non-interactive.</strong> The upgrade keeps existing config files on conflict (<code>--force-confold</code>) so it never hangs waiting for a prompt.</li>
              <li><strong>Confirmed.</strong> The browser asks for an explicit confirmation before any upgrade starts.</li>
            </ul>
            <p className="host-shell-settings-note">
              Non-Debian guests (no <code>apt</code>) are detected and reported as "nothing to upgrade" rather than
              failing. Kernel updates on the node still need a manual reboot.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
