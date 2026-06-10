import { Server } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import '@/styles/help-page.css'

export function HelpProxmoxSsh() {
  return (
    <div className="account-page account-stack">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Server className="h-5 w-5" /> Proxmox — SSH key
          </CardTitle>
          <CardDescription>
            SSH is <strong>optional</strong>. The API can report the node's own
            updates, but to count pending apt updates <em>inside each LXC
            container</em> Stashboard SSHes into the node and runs{' '}
            <code>pct exec &lt;vmid&gt; -- apt list --upgradable</code>. Leave the
            SSH fields blank to track only the node itself.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <section className="host-shell-settings-section">
            <h3>Generate a key pair</h3>
            <p>On any machine with OpenSSH (a dedicated key is cleanest):</p>
            <code className="help-code">ssh-keygen -t ed25519 -f ./stashboard_pve -C stashboard</code>
            <p>
              This writes <code>stashboard_pve</code> (private key) and{' '}
              <code>stashboard_pve.pub</code> (public key).
            </p>

            <h3>Authorize it on the node</h3>
            <p>Copy the public key to the user that can run <code>pct</code> (root, or a sudo-capable user):</p>
            <code className="help-code">ssh-copy-id -i stashboard_pve.pub root@&lt;node-ip&gt;</code>
            <p>Then verify the user can list containers:</p>
            <code className="help-code">ssh -i stashboard_pve root@&lt;node-ip&gt; pct list</code>

            <h3>Fill in Stashboard</h3>
            <ul className="host-shell-settings-conditions">
              <li><strong>SSH host / port</strong> — the node's IP/hostname and port (default <code>22</code>).</li>
              <li><strong>SSH username</strong> — <code>root</code>, or a user that can run <code>pct</code> via sudo.</li>
              <li>
                <strong>SSH private key (PEM)</strong> — paste the full contents
                of <code>stashboard_pve</code>, including the{' '}
                <code>-----BEGIN…</code> / <code>-----END…</code> lines.
              </li>
              <li><strong>Passphrase</strong> — only if you set one on the key.</li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
