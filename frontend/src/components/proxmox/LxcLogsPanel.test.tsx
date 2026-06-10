import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react'

// Mock the transport so the "all gates open" case doesn't try to mint a ticket
// or open a real WebSocket.
vi.mock('@/lib/proxmox-logs', () => ({ openProxmoxLogs: vi.fn() }))

import { openProxmoxLogs } from '@/lib/proxmox-logs'
import { LxcLogsPanel, type LxcLogsPanelProps } from './LxcLogsPanel'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockOpen = openProxmoxLogs as unknown as any

const OPEN: LxcLogsPanelProps = {
  connectionId: 'c1',
  vmId: 105,
  isRunning: true,
  sshConfigured: true,
  allowConsole: true,
  allowConsoleGlobal: true,
}

function renderPanel(overrides: Partial<LxcLogsPanelProps> = {}) {
  return render(<LxcLogsPanel {...OPEN} {...overrides} />)
}

beforeEach(() => {
  mockOpen.mockReset()
  // A never-closing live handle so the live view mounts without resolving.
  mockOpen.mockResolvedValue({ close: vi.fn() })
})

describe('LxcLogsPanel gating', () => {
  it('shows the global-switch hint when the server flag is off', () => {
    renderPanel({ allowConsoleGlobal: false })
    expect(screen.getByText('LXC logs are disabled on this server')).toBeInTheDocument()
    expect(mockOpen).not.toHaveBeenCalled()
  })

  it('shows the per-host hint when the host opt-in is off', () => {
    renderPanel({ allowConsole: false })
    expect(screen.getByText('LXC logs are not enabled for this host')).toBeInTheDocument()
    expect(mockOpen).not.toHaveBeenCalled()
  })

  it('shows the calm SSH hint when SSH is not configured', () => {
    renderPanel({ sshConfigured: false })
    expect(screen.getByText('This host has no SSH credentials')).toBeInTheDocument()
    expect(mockOpen).not.toHaveBeenCalled()
  })

  it('shows a clear empty state when the guest is not running', () => {
    renderPanel({ isRunning: false })
    expect(screen.getByText('Container is not running')).toBeInTheDocument()
    expect(mockOpen).not.toHaveBeenCalled()
  })

  it('starts a follow stream when every gate is open', async () => {
    renderPanel()
    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(1))
    expect(mockOpen).toHaveBeenCalledWith('c1', 105, expect.objectContaining({ follow: true }))
    expect(screen.getByText('Waiting for journal output…')).toBeInTheDocument()
  })
})

describe('LxcLogsPanel stream lifecycle — no session leak', () => {
  type Deferred = { resolve: (h: { close: ReturnType<typeof vi.fn> }) => void; handle: { close: ReturnType<typeof vi.fn> } }

  it('closes a superseded in-flight stream instead of leaking it', async () => {
    // Each open returns a controllable promise so we can resolve a *stale* run
    // after it was superseded (the StrictMode double-mount / rapid Stop→Stream
    // race that otherwise leaks a live SSH session against the per-user cap).
    const opens: Deferred[] = []
    mockOpen.mockReset()
    mockOpen.mockImplementation(() => new Promise<{ close: ReturnType<typeof vi.fn> }>((resolve) => {
      opens.push({ resolve, handle: { close: vi.fn() } })
    }))

    renderPanel()
    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(1))

    // Stop then Stream before run 1 resolves → run 1 is now superseded.
    fireEvent.click(screen.getByRole('button', { name: /Stop/ }))
    fireEvent.click(screen.getByRole('button', { name: /Stream/ }))
    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(2))

    // Resolve both runs. The stale run-1 handle must be closed (not leaked); the
    // current run-2 handle must be kept open.
    await act(async () => {
      opens[0].resolve(opens[0].handle)
      opens[1].resolve(opens[1].handle)
    })

    expect(opens[0].handle.close).toHaveBeenCalled()
    expect(opens[1].handle.close).not.toHaveBeenCalled()
  })

  it('closes the live handle when the panel unmounts', async () => {
    const closeSpy = vi.fn()
    mockOpen.mockReset()
    mockOpen.mockResolvedValue({ close: closeSpy })

    const { unmount } = renderPanel()
    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(1))
    // Let the ticket→handle promise settle so the live handle is stored.
    await act(async () => {})

    unmount()
    expect(closeSpy).toHaveBeenCalled()
  })

  it('surfaces a server close reason instead of dropping silently to idle', async () => {
    let opts: { onClose: (reason: string) => void } | undefined
    mockOpen.mockReset()
    mockOpen.mockImplementation((_c: string, _v: number, o: { onClose: (reason: string) => void }) => {
      opts = o
      return Promise.resolve({ close: vi.fn() })
    })

    renderPanel()
    await waitFor(() => expect(opts).toBeDefined())

    // The server rejected because the per-user session cap was reached.
    act(() => opts!.onClose('You already have 3 console session(s) open (limit 3).'))

    expect(await screen.findByText(/session\(s\) open \(limit 3\)/)).toBeInTheDocument()
  })
})
