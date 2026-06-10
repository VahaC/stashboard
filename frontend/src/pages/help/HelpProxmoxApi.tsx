import { KeyRound } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import '@/styles/help-page.css'

export function HelpProxmoxApi() {
  return (
    <div className="account-page account-stack">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <KeyRound className="h-5 w-5" /> Proxmox — API token
          </CardTitle>
          <CardDescription>
            The API token is what Stashboard uses for everything except per-LXC
            apt counts: discovering containers, reading node status, and the
            node's own pending updates. It's the minimum a Proxmox host needs.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <section className="host-shell-settings-section">
            <h3>Create the token</h3>
            <ol className="host-shell-settings-conditions">
              <li>
                In the Proxmox web UI, go to <strong>Datacenter → Permissions →
                API Tokens</strong> and click <strong>Add</strong>.
              </li>
              <li>
                Pick a <strong>User</strong> (e.g. <code>root@pam</code>) and a{' '}
                <strong>Token ID</strong> (e.g. <code>stashboard</code>). For a
                read-only homelab setup the simplest path is to{' '}
                <strong>uncheck "Privilege Separation"</strong> so the token
                inherits the user's permissions.
              </li>
              <li>
                Click <strong>Add</strong>. Proxmox shows the secret{' '}
                <strong>once</strong> — copy it now.
              </li>
              <li>
                If you left Privilege Separation on, also grant the token a role
                under <strong>Datacenter → Permissions</strong>:{' '}
                <code>PVEAuditor</code> on path <code>/</code> is enough for
                discovery and update reads.
              </li>
            </ol>

            <h3>Fill in Stashboard</h3>
            <ul className="host-shell-settings-conditions">
              <li>
                <strong>API base URL</strong> —{' '}
                <code>https://&lt;node-ip&gt;:8006</code>
              </li>
              <li>
                <strong>Node name</strong> — the name in the left tree, usually{' '}
                <code>pve</code>.
              </li>
              <li>
                <strong>API token ID</strong> —{' '}
                <code>user@realm!tokenid</code>, e.g.{' '}
                <code>root@pam!stashboard</code>.
              </li>
              <li>
                <strong>API token secret</strong> — the value you copied.
              </li>
              <li>
                <strong>Skip TLS validation</strong> — leave on for the
                self-signed certificate most homelab installs ship with.
              </li>
            </ul>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}
