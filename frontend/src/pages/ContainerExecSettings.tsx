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
 * V5.7 — Settings → Container exec. The app-wide master switch for the browser
 * container-exec terminal (an interactive shell inside a container, via the
 * Docker daemon's exec API). Like the host terminal, the page leads with the
 * risks and spells out every condition required before a shell can be opened.
 */
export function ContainerExecSettings() {
  const queryClient = useQueryClient()
  const [enabled, setEnabled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)

  useEffect(() => {
    settingsApi.getContainerExecSettings()
      .then((s) => setEnabled(s.enabled))
      .catch(() => setEnabled(false))
      .finally(() => setLoaded(true))
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    try {
      await settingsApi.updateContainerExecSettings({ enabled })
      // The Exec tab gates on the cached feature flag — refresh it so the
      // change takes effect without a reload.
      await queryClient.invalidateQueries({ queryKey: qk.features })
      setMessage({ kind: 'ok', text: enabled ? 'Container exec enabled server-wide.' : 'Container exec disabled.' })
    } catch (error: unknown) {
      const { globalError } = parseApiErrors(error)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save container-exec setting.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Container exec</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <SquareChevronRight className="h-5 w-5" /> Container exec terminal
          </CardTitle>
          <CardDescription>
            Opens an interactive shell <strong>inside a running container</strong> from a container's
            <strong> Exec</strong> tab, via the Docker daemon's <code>exec</code> API. Use it for the
            "I just need to run one command in this container" case — <code>cat</code> a config,
            check a process, run a CLI bundled in the image — without SSHing to the host first.
            Unlike the host terminal, it works for <strong>every</strong> connection type (local socket,
            TCP+TLS, SSH tunnel), because it routes through the daemon rather than an SSH login.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="host-shell-settings-warning">
            <AlertTriangle className="h-4 w-4 shrink-0" />
            <div>
              <strong>This runs arbitrary commands inside your workloads.</strong> Anyone who can open
              an exec session gets a full shell in the container with that process's privileges — which,
              for containers running as root (most), is effectively root in that container, and can be a
              path to the host if the container is privileged. Leave it off unless you understand and
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
              Enable container exec server-wide
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
              for a connection only when <strong>both</strong> conditions hold:
            </p>
            <ol className="host-shell-settings-conditions">
              <li>
                <strong>This setting is on</strong> (server-wide master switch — what you toggle above).
              </li>
              <li>
                <strong>The connection has opted in.</strong> Open the Docker connection and tick
                <strong> “Allow container exec”</strong>. It's off by default and set per connection,
                so enabling one host never enables another.
              </li>
            </ol>
            <p>
              If either condition is missing, the ticket request is refused server-side and no WebSocket
              is opened — the gate isn't just a hidden button. The container must also be running.
            </p>
          </section>

          <section className="host-shell-settings-section">
            <h3>Guardrails (always enforced)</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Audited.</strong> Every session records who connected, when, to which container, which command, how long, how many bytes flowed, and why it ended — plus a line in the application log.</li>
              <li><strong>Concurrency caps.</strong> Per-user and per-host limits on simultaneous sessions (tunable via <code>STASHBOARD_Stashboard__ContainerExec__*</code>).</li>
              <li><strong>Idle timeout.</strong> The server closes inactive sessions regardless of whether the browser tab is still open.</li>
              <li><strong>No token leakage.</strong> The WebSocket is authorised by a single-use, short-lived ticket — never a token on the query string. The chosen command is bound to the ticket server-side.</li>
            </ul>
            <p className="host-shell-settings-note">
              The default command is <code>/bin/sh</code>; you can change it per session (e.g.
              <code> /bin/bash</code>) in the Exec panel before connecting. Live terminal resize is
              supported.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
