import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArchiveRestore } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V8.1 / V8.3 — Settings → Restore guest. The app-wide master switch for re-creating a
 * guest from a vzdump backup archive — an LXC container (`POST /nodes/{node}/lxc` +
 * `restore=1`) or a QEMU VM (`POST /nodes/{node}/qemu` + `archive=…`, V8.3). Like the
 * create / destroy pages, it leads with what the action does and spells out every
 * condition required before the Restore LXC / Restore VM buttons appear.
 */
export function ProxmoxRestoreSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxRestoreSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxRestoreSettings({ enabled })
      // The Restore LXC / Restore VM affordances gate on the cached feature flag —
      // refresh it so the change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Restore enabled server-wide.' : 'Restore disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save restore setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Restore guest (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ArchiveRestore className="h-5 w-5" /> Restore guest
          </CardTitle>
          <CardDescription>
            Adds <strong>Restore LXC</strong> and <strong>Restore VM</strong> buttons to a Proxmox host's header on the
            Proxmox page — re-create a container (<code>POST /nodes/&#123;node&#125;/lxc</code> with <code>restore=1</code>)
            or a QEMU VM (<code>POST /nodes/&#123;node&#125;/qemu</code> with <code>archive=…</code>) from an existing
            <code> vzdump</code> backup archive without leaving Stashboard, provisioning the guest and its disks from the
            archive.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This re-creates a guest — and can overwrite an existing one.</strong> A restore consumes disk on
              the target storage. Restoring <em>over</em> an existing VMID replaces that guest and is irreversible. It
              is off by default — leave it off unless you want operators to be able to restore from the dashboard.
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
              Enable guest restore server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the Restore LXC / Restore VM buttons appear
              for a host only when <strong>both</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li><strong>This setting is on</strong> (server-wide master switch — what you toggle above).</li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow restore”</strong>. It's off by default and set per host.
              </li>
            </ol>
            <p>
              If either condition is missing, the restore request is refused server-side — the gate isn't just a hidden
              button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Validated.</strong> The VMID range and backup volid are checked before the request leaves Stashboard.</li>
              <li><strong>Overwrite is gated.</strong> Restoring over an existing VMID requires that guest to be <em>stopped</em> and an explicit double confirmation naming the target.</li>
              <li><strong>Proxmox is authoritative.</strong> A storage that can't hold the disks, a missing archive, or any other host rejection is surfaced verbatim — nothing is silently swallowed.</li>
              <li><strong>Audited.</strong> Every attempt records who triggered it, when, against which host / node / vmid / backup, whether it overwrote an existing guest, and the result — on the Audit page's <strong>Guest restore</strong> tab.</li>
            </ul>
            <p className="host-shell-settings-note">
              Restoring from a Proxmox Backup Server datastore (<code>pbs:</code> volumes) is out of scope — only
              file-based <code>vzdump-lxc-*</code> / <code>vzdump-qemu-*</code> archives on the node's storages are offered.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
