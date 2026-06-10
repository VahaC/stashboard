import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, TerminalSquare } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { settingsApi } from '@/lib/settings-api'
import { qk } from '@/lib/queries'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/account-page.css'

/**
 * V5.3 — Settings → Host terminal. The app-wide master switch for the browser
 * host terminal (an interactive SSH shell on a Docker host). This is the most
 * dangerous feature in Stashboard, so the page leads with the risks and spells
 * out every condition required before a shell can actually be opened.
 */
export function HostTerminalSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getHostShellSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateHostShellSettings({ enabled })
      // The Terminal tab gates on the cached feature flag — refresh it so the
      // change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Host terminal enabled server-wide.' : 'Host terminal disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save host-terminal setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Host terminal (Docker)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <TerminalSquare className="h-5 w-5" /> Docker host terminal
          </CardTitle>
          <CardDescription>
            Opens an interactive shell <strong>on the Docker host itself</strong> (not inside a
            container) from a container's <strong>Terminal</strong> tab, over the connection's SSH
            tunnel. Use it when you need a shell on the box — <code>df -h</code>, <code>journalctl</code>,
            editing a compose file — without leaving Stashboard.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This is the most dangerous feature in Stashboard.</strong> A host shell is full,
              interactive, host-level remote access — anyone who can open it can do anything the SSH
              user can, including escalate to root on most setups. Leave it off unless you understand
              and accept that. It is off by default.
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
              Enable the host terminal server-wide
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
              This switch is only the <em>global gate</em>. Even with it on, the live terminal appears
              for a connection only when <strong>all three</strong> conditions hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li>
                <strong>This setting is on</strong> (server-wide master switch — what you toggle above).
              </li>
              <li>
                <strong>The connection has opted in.</strong> Open the Docker connection and tick
                <strong> “Allow host terminal”</strong>. It's off by default and set per connection,
                so enabling one host never enables another.
              </li>
              <li>
                <strong>The connection is an SSH tunnel.</strong> Local-socket and TCP+TLS connections
                have no host shell to offer — their Terminal tab shows
                <em> “Available only for SSH tunnel connections.”</em> The shell reuses the SSH key
                already configured on that connection.
              </li>
            </ol>
            <p>
              If any condition is missing, the ticket request is refused server-side and no WebSocket
              is opened — the gate isn't just a hidden button.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Audited.</strong> Every session records who connected, when, to which host, how long, how many bytes flowed, and why it ended — plus a line in the application log.</li>
              <li><strong>Concurrency caps.</strong> Per-user and per-host limits on simultaneous sessions (tunable via <code>STASHBOARD_Stashboard__HostShell__*</code>).</li>
              <li><strong>Idle timeout.</strong> The server closes inactive sessions regardless of whether the browser tab is still open.</li>
              <li><strong>No token leakage.</strong> The WebSocket is authorised by a single-use, short-lived ticket — never a token on the query string.</li>
            </ul>
            <p className="host-shell-settings-note">
              Note: the terminal opens at your browser window's size; live resizing isn't supported by
              the SSH library, so reconnect to pick up a new size.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
