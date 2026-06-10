import { ShieldCheck } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import '@/styles/help-page.css'

export function HelpDockerTls() {
  return (
    <div className="account-page account-stack">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ShieldCheck className="h-5 w-5" /> Docker — TCP + TLS
          </CardTitle>
          <CardDescription>
            Connects to the Docker daemon over the network with mutual TLS. More
            moving parts than SSH and it exposes the daemon on a port — only use
            it if you can't use SSH, and firewall the port tightly.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <section className="host-shell-settings-section">
            <h3>Generate CA + certificates</h3>
            <p>
              Follow Docker's official guide,{' '}
              <a
                href="https://docs.docker.com/engine/security/protect-access/"
                target="_blank"
                rel="noreferrer"
                className="text-[var(--primary)] underline"
              >
                Protect the Docker daemon socket
              </a>
              . It produces three files Stashboard needs: a CA cert
              (<code>ca.pem</code>), a client cert (<code>cert.pem</code>) and a
              client key (<code>key.pem</code>).
            </p>

            <h3>Make the daemon listen with TLS</h3>
            <p>
              Configure the daemon to verify clients and listen on{' '}
              <code>2376</code>. With systemd, add a drop-in
              (<code>/etc/systemd/system/docker.service.d/override.conf</code>)
              or set it in <code>/etc/docker/daemon.json</code>, e.g.:
            </p>
            <code className="help-code">{`{
  "hosts": ["unix:///var/run/docker.sock", "tcp://0.0.0.0:2376"],
  "tlsverify": true,
  "tlscacert": "/etc/docker/certs/ca.pem",
  "tlscert": "/etc/docker/certs/server-cert.pem",
  "tlskey": "/etc/docker/certs/server-key.pem"
}`}</code>
            <p>Reload and restart: <code>sudo systemctl daemon-reload &amp;&amp; sudo systemctl restart docker</code>.</p>

            <h3>Fill in Stashboard</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>Docker host</strong> — <code>Remote TCP+TLS</code>.</li>
              <li><strong>Host URL</strong> — <code>tcp://&lt;host&gt;:2376</code>.</li>
              <li><strong>TLS CA cert</strong> — paste <code>ca.pem</code>.</li>
              <li><strong>TLS client cert</strong> — paste <code>cert.pem</code>.</li>
              <li><strong>TLS client key</strong> — paste <code>key.pem</code>.</li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
