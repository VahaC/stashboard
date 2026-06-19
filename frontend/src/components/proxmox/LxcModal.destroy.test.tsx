import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }))

// useFeatures drives the Destroy gate. Default it on for these tests; individual
// tests can't easily re-mock, so the connection's allowDestroy carries the
// off-case.
vi.mock('@/lib/queries', () => ({
  useFeatures: () => ({ data: { allowProxmoxDestroy: true, allowProxmoxUpdates: false, allowProxmoxConsole: false } }),
  useServices: () => ({ data: [] }),
}))

import { api } from '@/lib/api'
import { LxcModal } from './LxcModal'
import type { ProxmoxConnection, ProxmoxGuest } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any; delete: any }

const connection = {
  id: 'c1', nodeName: 'pve', name: 'home', allowDestroy: true,
  hasSshPrivateKey: false, sshHost: null, sshUsername: null,
} as unknown as ProxmoxConnection

function stoppedGuest(): ProxmoxGuest {
  return { vmId: 101, name: 'wg', isRunning: false, tags: [] } as unknown as ProxmoxGuest
}

function renderModal(guest: ProxmoxGuest, conn: ProxmoxConnection = connection, onClose = () => {}) {
  mockApi.get.mockResolvedValue({ data: {} })
  mockApi.delete.mockResolvedValue({ data: null })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <LxcModal guest={guest} connection={conn} initialTab="overview" onClose={onClose} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.delete.mockReset()
})

describe('LxcModal Destroy — V6.13', () => {
  it('confirm dialog names the exact target (CT <vmid> · <name>)', async () => {
    renderModal(stoppedGuest())

    fireEvent.click(await screen.findByRole('button', { name: /Destroy/ }))

    expect(await screen.findByText('Destroy container?')).toBeInTheDocument()
    expect(screen.getByText('CT 101 · wg')).toBeInTheDocument()
  })

  it('confirming issues the DELETE and closes the modal', async () => {
    const onClose = vi.fn()
    renderModal(stoppedGuest(), connection, onClose)

    fireEvent.click(await screen.findByRole('button', { name: /Destroy/ }))
    fireEvent.click(await screen.findByRole('button', { name: /Destroy container/ }))

    await waitFor(() =>
      expect(mockApi.delete).toHaveBeenCalledWith('/api/proxmox/connections/c1/lxc/101'))
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('hides the Destroy button when the host has not opted in', async () => {
    renderModal(stoppedGuest(), { ...connection, allowDestroy: false } as ProxmoxConnection)

    await screen.findByText('Lifecycle')
    expect(screen.queryByRole('button', { name: /Destroy/ })).not.toBeInTheDocument()
  })

  it('hides the Destroy button while the guest is running', async () => {
    renderModal({ ...stoppedGuest(), isRunning: true } as ProxmoxGuest)

    await screen.findByText('Lifecycle')
    expect(screen.queryByRole('button', { name: /Destroy/ })).not.toBeInTheDocument()
  })

  // V6.14 — destroy now works for QEMU VMs too, routed to the qemu endpoint.
  it('destroys a VM via the qemu endpoint, with VM-worded confirmation', async () => {
    const onClose = vi.fn()
    const vm = { vmId: 200, name: 'win11', guestType: 'Qemu', isRunning: false, tags: [] } as unknown as ProxmoxGuest
    renderModal(vm, connection, onClose)

    fireEvent.click(await screen.findByRole('button', { name: /Destroy/ }))
    expect(await screen.findByText('Destroy VM?')).toBeInTheDocument()
    expect(screen.getByText('VM 200 · win11')).toBeInTheDocument()

    fireEvent.click(await screen.findByRole('button', { name: /Destroy VM/ }))
    await waitFor(() =>
      expect(mockApi.delete).toHaveBeenCalledWith('/api/proxmox/connections/c1/qemu/200'))
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })
})
