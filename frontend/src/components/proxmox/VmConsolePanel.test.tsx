import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { VmConsolePanel } from '@/components/proxmox/VmConsolePanel'

/**
 * V8.6 — the VM console panel's triple gate. The panel never constructs noVNC until
 * the user clicks Connect, so these tests exercise the gating disabled-states and the
 * ready-to-connect affordance without a live RFB session. Unlike the LXC console there
 * is no SSH gate — a VM's VNC relay uses the API token.
 */
function renderPanel(props: Partial<React.ComponentProps<typeof VmConsolePanel>> = {}) {
  return render(
    <MemoryRouter>
      <VmConsolePanel
        connectionId="c1"
        vmId={200}
        isRunning
        allowConsole
        allowConsoleGlobal
        {...props}
      />
    </MemoryRouter>,
  )
}

describe('VmConsolePanel — gating', () => {
  it('blocks when the server-wide switch is off', () => {
    renderPanel({ allowConsoleGlobal: false })
    expect(screen.getByText('The guest console is disabled on this server')).toBeInTheDocument()
  })

  it('blocks when the host has not opted in', () => {
    renderPanel({ allowConsole: false })
    expect(screen.getByText('The console is not enabled for this host')).toBeInTheDocument()
  })

  it('blocks when the VM is not running', () => {
    renderPanel({ isRunning: false })
    expect(screen.getByText('VM is not running')).toBeInTheDocument()
  })

  it('does NOT require SSH (no SSH disabled-state) and offers Connect when all gates pass', () => {
    renderPanel()
    expect(screen.queryByText(/SSH/i)).toBeNull()
    expect(screen.getByRole('button', { name: /Connect/ })).toBeInTheDocument()
  })
})
