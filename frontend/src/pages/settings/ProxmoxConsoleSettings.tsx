import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, SquareChevronRight } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V6.6 / V8.6 — Settings → Guest console. The app-wide master switch for the browser
 * Proxmox guest console. Despite its original "LXC console" name it governs **both**
 * guest kinds: the LXC shell (SSH to the host + `pct exec`) and the VM VNC screen
 * (noVNC, relayed server-side over the API token) — and the LXC live-logs tail, which
 * shares the same SSH channel. Like the host terminal and container exec, the page leads
 * with the risks and spells out every condition required before a console can be opened.
 */
export function ProxmoxConsoleSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getProxmoxConsoleSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateProxmoxConsoleSettings({ enabled })
      // The Console tab gates on the cached feature flag — refresh it so the
      // change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Guest console enabled server-wide.' : 'Guest console disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save guest-console setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Guest console (Proxmox)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <SquareChevronRight className="h-5 w-5" /> Proxmox guest console
          </CardTitle>
          <CardDescription>
            Opens an interactive shell <strong>inside a running LXC</strong> from a container's
            <strong> Console</strong> tab on the Proxmox page. Stashboard SSHes to the Proxmox host and runs
            <code> pct exec &lt;vmid&gt; -- /bin/bash</code>, so you reach the guest without managing a key per
            container. Use it for the "I need to <code>apt upgrade</code> this LXC" case — the natural
            follow-up to the per-LXC update count — without leaving the browser.
            <br />
            <strong>This same switch also governs the VM console</strong> (V8.6): a running QEMU/KVM VM's
            <strong> Console</strong> tab shows its built-in <strong>VNC</strong> screen with noVNC — the same
            screen the Proxmox web UI opens — relayed server-side. A VM needs no SSH (VNC uses the API token).
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This runs arbitrary commands inside your containers — usually as root.</strong> The SSH
              user must be able to run <code>pct</code> (root, or a sudo-capable account), so anyone who can open
              a console session effectively gets a root shell in the guest. Leave it off unless you understand and
              accept that. It is off by default.
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
              Enable the guest console server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the live console appears for an LXC
              only when <strong>all</strong> of these hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li>
                <strong>This setting is on</strong> (server-wide master switch — what you toggle above).
              </li>
              <li>
                <strong>The host has opted in.</strong> Open the Proxmox host and tick
                <strong> “Allow guest console”</strong>. It's off by default and set per host, so enabling one host
                never enables another.
              </li>
              <li>
                <strong>SSH is configured</strong> on the host (host / username / private key) — the LXC console
                SSHes in and runs <code>pct exec</code>. <em>VM consoles skip this</em> — the VNC relay uses the
                API token, so a VM needs no SSH.
              </li>
            </ol>
            <p>
              If any condition is missing, the ticket request is refused server-side and no WebSocket is opened —
              the gate isn't just a hidden button. The guest (LXC or VM) must also be running.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Audited.</strong> Every session records who connected, when, to which host / node / guest, which command, how long, how many bytes flowed, and why it ended — plus a line in the application log.</li>
              <li><strong>Concurrency caps.</strong> Per-user and per-host limits on simultaneous sessions (tunable via <code>STASHBOARD_Stashboard__ProxmoxConsole__*</code>).</li>
              <li><strong>Idle timeout.</strong> The server closes inactive sessions regardless of whether the browser tab is still open.</li>
              <li><strong>No token leakage.</strong> The WebSocket is authorised by a single-use, short-lived ticket — never a token on the query string. The chosen command is bound to the ticket server-side.</li>
            </ul>
            <p className="host-shell-settings-note">
              The default command is <code>/bin/bash</code>; you can change it per session (e.g.
              <code> /bin/sh</code> for an Alpine guest) in the Console panel before connecting. Live terminal
              resize is unavailable over SSH — the shell is created at the size your browser reports on connect.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
