import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Trash2 } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V6.13 — Settings → Destroy LXC. The app-wide master switch for the irreversible
 * destroy action (`DELETE /nodes/{node}/lxc/{vmid}`). Like the LXC console and
 * Proxmox updates, the page leads with the risks and spells out every condition
 * required before the Destroy button appears.
 */
export function ProxmoxDestroySettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxDestroySettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxDestroySettings({ enabled })
      // The Destroy affordance gates on the cached feature flag — refresh it so
      // the change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Destroy enabled server-wide.' : 'Destroy disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save destroy-LXC setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Destroy LXC (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Trash2 className="h-5 w-5" /> Destroy LXC
          </CardTitle>
          <CardDescription>
            Adds a <strong>Destroy</strong> button to a stopped LXC's Lifecycle section on the Proxmox page — the
            container analogue of Docker's "Remove container". Stashboard calls
            <code> DELETE /nodes/&#123;node&#125;/lxc/&#123;vmid&#125;</code> on the Proxmox API, which removes the
            container and its root disk.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This permanently destroys the container and its disk.</strong> There is no undo — the
              container's writable storage is gone. Associated backups and external storage volumes are not touched.
              Leave it off unless you understand and accept that. It is off by default.
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
              Enable destroy LXC server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the Destroy button appears for a guest
              only when <strong>all</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li><strong>This setting is on</strong> (server-wide master switch — what you toggle above).</li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow destroy”</strong>. It's off by default and set per host.
              </li>
              <li><strong>The container is stopped.</strong> A running guest is refused — stop it first.</li>
            </ol>
            <p>
              If any condition is missing, the destroy request is refused server-side — the gate isn't just a hidden
              button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Double confirmation.</strong> The browser names the exact guest (<code>CT &lt;vmid&gt; · &lt;name&gt;</code>) and asks for an explicit destructive click before anything runs.</li>
              <li><strong>Stopped only.</strong> A running guest is rejected with a clear "stop first" message — server-side, not just in the UI.</li>
              <li><strong>Audited.</strong> Every attempt records who triggered it, when, against which host / node / guest, and the result — on the Audit page's <strong>LXC destroy</strong> tab.</li>
            </ul>
            <p className="host-shell-settings-note">
              Purging associated backups or external storage volumes is out of scope — only the container and its root
              disk are removed.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
