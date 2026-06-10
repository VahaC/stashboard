import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Plus } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V6.13.1 — Settings → Create LXC. The app-wide master switch for provisioning a
 * container from a template (`POST /nodes/{node}/lxc`). Like the destroy / updates
 * pages, it leads with what the action does and spells out every condition
 * required before the New LXC button appears.
 */
export function ProxmoxCreateSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxCreateSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxCreateSettings({ enabled })
      // The New LXC affordance gates on the cached feature flag — refresh it so
      // the change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Create enabled server-wide.' : 'Create disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save create-LXC setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Create LXC (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Plus className="h-5 w-5" /> Create LXC
          </CardTitle>
          <CardDescription>
            Adds a <strong>New LXC</strong> button to a Proxmox host's header on the Proxmox page — provision a container
            from an existing template without leaving Stashboard. It calls
            <code> POST /nodes/&#123;node&#125;/lxc</code> on the Proxmox API, which creates the container and its root disk.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This provisions real storage and network on the host.</strong> A new container consumes disk on the
              chosen storage and attaches to the chosen bridge. It is off by default — leave it off unless you want
              operators to be able to create containers from the dashboard.
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
              Enable create LXC server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the New LXC button appears for a host only
              when <strong>both</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li><strong>This setting is on</strong> (server-wide master switch — what you toggle above).</li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow create”</strong>. It's off by default and set per host.
              </li>
            </ol>
            <p>
              If either condition is missing, the create request is refused server-side — the gate isn't just a hidden
              button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Validated.</strong> The VMID range, network (CIDR / MAC / VLAN) and sizes are checked before the request leaves Stashboard; a VMID already in use is rejected.</li>
              <li><strong>Proxmox is authoritative.</strong> A storage that can't hold a rootfs, a missing template, or any other host rejection is surfaced verbatim — nothing is silently swallowed.</li>
              <li><strong>Audited.</strong> Every attempt records who triggered it, when, against which host / node / vmid / hostname / template, and the result — on the Audit page's <strong>LXC create</strong> tab.</li>
            </ul>
            <p className="host-shell-settings-note">
              Cloning, restoring from a backup, and advanced multi-mount rootfs layouts are out of scope — create the
              container, then edit it from its Config tab.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
