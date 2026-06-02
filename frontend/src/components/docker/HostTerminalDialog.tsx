import { Link } from 'react-router-dom'
import { ScrollText } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { HostTerminalPanel } from '@/components/docker/HostTerminalPanel'
import { resolveDockerHostType, type DockerConnection } from '@/lib/types'

export interface HostTerminalDialogProps {
  connection: DockerConnection
  /** Global host-terminal master switch (StashboardFeatures.allowHostShell). */
  allowHostShellGlobal: boolean
  onClose: () => void
}

/**
 * V5.7 — the host terminal moved from the container modal to the **connection**
 * level, where it belongs: it opens an interactive SSH shell on the *host* of a
 * connection, not inside any one container. Triggered from the connection header
 * on the Docker instances page. The live terminal / disabled explainers are all
 * handled by {@link HostTerminalPanel}.
 */
export function HostTerminalDialog({ connection, allowHostShellGlobal, onClose }: HostTerminalDialogProps) {
  const hostLabel = connection.sshHost
    ? `${connection.sshHost}${connection.sshPort ? `:${connection.sshPort}` : ''}`
    : resolveDockerHostType(connection.hostType)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle>Host terminal · {connection.name}</DialogTitle>
          <DialogDescription>
            Interactive shell on the Docker host ({hostLabel}) — not inside a container.
          </DialogDescription>
        </DialogHeader>
        <div className="container-modal-body" role="tabpanel">
          <HostTerminalPanel
            connectionId={connection.id}
            hostType={resolveDockerHostType(connection.hostType)}
            allowHostShell={connection.allowHostShell}
            allowHostShellGlobal={allowHostShellGlobal}
          />
          <p className="mt-3 text-xs">
            <Link
              to={`/audit?tab=host&connectionId=${connection.id}`}
              onClick={onClose}
              className="text-[var(--primary)] underline inline-flex items-center gap-1"
            >
              <ScrollText className="h-3.5 w-3.5" /> View session history
            </Link>
          </p>
        </div>
      </DialogContent>
    </Dialog>
  )
}
