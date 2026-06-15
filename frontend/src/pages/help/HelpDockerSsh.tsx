import { Container } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import '@/styles/help-page.css'

export function HelpDockerSsh() {
  return (
    <div className="account-page account-stack">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Container className="h-5 w-5" /> Docker — SSH tunnel
          </CardTitle>
          <CardDescription>
            The easiest way to reach a remote Docker host. Stashboard opens an
            SSH connection per check and bridges{' '}
            <code>docker system dial-stdio</code> — nothing is exposed on the
            network. Recommended unless you specifically need TCP+TLS.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <section className="host-shell-settings-section">
            <h3>Generate a key &amp; authorize it</h3>
            <code className="help-code">ssh-keygen -t ed25519 -f ./stashboard_docker -C stashboard<br />
              ssh-copy-id -i stashboard_docker.pub docker@&lt;host&gt;</code>
            <h3>Give the user socket access</h3>
            <p>The SSH user must be in the <code>docker</code> group (or own the socket):</p>
            <code className="help-code">sudo usermod -aG docker docker   # then log out and back in</code>
            <p>Verify over SSH:</p>
            <code className="help-code">ssh -i stashboard_docker docker@&lt;host&gt; docker ps</code>

            <h3>Fill in Stashboard</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Docker host</strong> — <code>SSH tunnel</code>.</li>
              <li><strong>SSH host / port / username</strong> — your host, <code>22</code>, the user above.</li>
              <li><strong>SSH private key (PEM)</strong> — paste the full <code>stashboard_docker</code> contents.</li>
              <li>
                <strong>Remote socket path</strong> —{' '}
                <code>/var/run/docker.sock</code>, or{' '}
                <code>/run/user/&lt;uid&gt;/docker.sock</code> for rootless Docker.
              </li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
