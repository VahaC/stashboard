import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }))
// The Logs gate needs the global console flag on; everything else inert.
vi.mock('@/lib/queries', () => ({
  useFeatures: () => ({ data: { allowProxmoxUpdates: false, allowProxmoxConsole: true } }),
  useServices: () => ({ data: [] }),
}))
// Keep the SSH transport out of the test — just observe open/close.
vi.mock('@/lib/proxmox-logs', () => ({ openProxmoxLogs: vi.fn() }))

import { api } from '@/lib/api'
import { openProxmoxLogs } from '@/lib/proxmox-logs'
import { LxcModal } from './LxcModal'
import type { ProxmoxConnection, ProxmoxGuest } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any }
const mockOpen = openProxmoxLogs as unknown as any

// SSH configured + opted-in so the Logs panel reaches the live view.
const connection = {
  id: 'c1', nodeName: 'pve', name: 'home',
  hasSshPrivateKey: true, sshHost: 'pve.lan', sshUsername: 'root', allowConsole: true,
} as unknown as ProxmoxConnection
const guest = { vmId: 101, name: 'wg', isRunning: true, tags: [] } as unknown as ProxmoxGuest

function renderModal() {
  mockApi.get.mockResolvedValue({ data: {} })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <LxcModal guest={guest} connection={connection} initialTab="logs" onClose={() => {}} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.put.mockReset()
  mockApi.post.mockReset()
  mockOpen.mockReset()
})

describe('LxcModal — SSH session lifecycle across tabs (V6.12)', () => {
  it('keeps the Logs stream alive when switching tabs, and never restarts it', async () => {
    mockOpen.mockResolvedValue({ close: vi.fn() })
    renderModal()

    // Opening on Logs starts exactly one follow stream.
    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(1))
    expect(mockOpen).toHaveBeenCalledWith('c1', 101, expect.objectContaining({ follow: true }))

    // Switch away to Overview and back — the panel stays mounted (hidden), so no
    // new stream is opened.
    fireEvent.click(screen.getByRole('tab', { name: /Overview/ }))
    fireEvent.click(screen.getByRole('tab', { name: /Logs/ }))

    await waitFor(() => expect(screen.getByText(/Waiting for journal output…/)).toBeInTheDocument())
    expect(mockOpen).toHaveBeenCalledTimes(1)
  })

  it('closes the SSH session when the modal unmounts', async () => {
    const closeSpy = vi.fn()
    mockOpen.mockResolvedValue({ close: closeSpy })
    const { unmount } = renderModal()

    await waitFor(() => expect(mockOpen).toHaveBeenCalledTimes(1))
    // Let the ticket→handle promise settle so the live handle is stored.
    await waitFor(() => {})

    unmount()
    await waitFor(() => expect(closeSpy).toHaveBeenCalled())
  })
})
