import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }))
vi.mock('@/lib/queries', () => ({
  useFeatures: () => ({ data: { allowProxmoxUpdates: false, allowProxmoxConsole: false, allowProxmoxDestroy: false } }),
  useServices: () => ({ data: [] }),
}))

import { api } from '@/lib/api'
import { LxcModal } from './LxcModal'
import type { ProxmoxConnection, ProxmoxGuest, ProxmoxLxcDetail } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any }

const connection = { id: 'c1', nodeName: 'pve', name: 'home' } as unknown as ProxmoxConnection
const vmGuest = { vmId: 200, name: 'win11', guestType: 'Qemu', isRunning: true, tags: [] } as unknown as ProxmoxGuest

const vmDetail: ProxmoxLxcDetail = {
  vmId: 200, status: 'running', cores: 4, memoryBytes: 8 * 1024 * 1024 * 1024, swapBytes: null,
  hostname: 'win11', osType: 'win11', arch: null, onboot: true, unprivileged: null, features: null,
  networks: [{ key: 'net0', value: 'virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0' }],
  mounts: [{ key: 'scsi0', value: 'local-lvm:vm-200-disk-0,size=64G' }],
  cpuFraction: 0.1, memUsedBytes: 100, memMaxBytes: 8 * 1024 * 1024 * 1024,
  diskUsedBytes: 1, diskMaxBytes: 64 * 1024 * 1024 * 1024, uptimeSeconds: 100,
  // V8.5 — VM-editable fields.
  sockets: 1, agent: true, bootOrder: 'scsi0;ide2;net0', description: null, tags: null, balloonBytes: null,
}

function renderModal(initialTab: 'overview' | 'config' = 'overview') {
  // Route reads by path: the config tab fetches the detail, while the VM editor's
  // ISO / storage dropdowns expect arrays.
  mockApi.get.mockImplementation((url: string) => {
    if (url.includes('/config')) return Promise.resolve({ data: vmDetail })
    if (url.includes('/isos') || url.includes('/node/storage')) return Promise.resolve({ data: [] })
    return Promise.resolve({ data: [] })
  })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <LxcModal guest={vmGuest} connection={connection} initialTab={initialTab} onClose={() => {}} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.put.mockReset()
  mockApi.post.mockReset()
})

/**
 * V6.14 — a QEMU VM reuses the LXC modal shell but exposes only the tabs that
 * generalise to a VM (Overview · Config · Tasks · Stats), with a read-only
 * Config tab and a "VM <vmid>" subtitle. The SSH/apt/pct-backed tabs (Watch,
 * Logs, Console) are LXC-only.
 */
describe('LxcModal — QEMU VM variant (V6.14)', () => {
  it('shows only the VM-applicable tabs (no Watch / Logs / Console)', () => {
    renderModal()
    const tabs = screen.getAllByRole('tab').map((t) => t.textContent?.trim())
    expect(tabs).toEqual(['Overview', 'Config', 'Tasks', 'Stats'])
  })

  it('renders the VM subtitle and "Virtual machine" overview heading', () => {
    renderModal()
    expect(screen.getByText(/VM 200 · pve/)).toBeInTheDocument()
    expect(screen.getByText('Virtual machine')).toBeInTheDocument()
  })

  it('Shutdown asks for confirmation, then posts to the qemu lifecycle endpoint', async () => {
    mockApi.post.mockResolvedValue({ data: {} })
    renderModal()   // overview tab (lifecycle lives here)

    // The card/overview Shutdown opens a confirm dialog explaining what it does.
    fireEvent.click(screen.getByRole('button', { name: 'Shutdown' }))
    expect(await screen.findByText(/Shut down VM\?/)).toBeInTheDocument()
    expect(screen.getByText(/power off cleanly/i)).toBeInTheDocument()
    // No lifecycle call until the user confirms.
    expect(mockApi.post).not.toHaveBeenCalled()

    // Confirming runs the action against the QEMU path (not lxc/*).
    const confirms = screen.getAllByRole('button', { name: 'Shutdown' })
    fireEvent.click(confirms[confirms.length - 1])
    await waitFor(() =>
      expect(mockApi.post).toHaveBeenCalledWith(expect.stringContaining('/qemu/200/status/shutdown')),
    )
  })

  it('reads the VM config from the qemu endpoint and is now editable (V8.5)', async () => {
    renderModal('config')
    // The Config tab fetches from the qemu/* path, not lxc/*.
    await screen.findByText('Disks')
    expect(mockApi.get).toHaveBeenCalledWith(
      expect.stringContaining('/qemu/200/config'),
    )
    // V8.5 — the VM Config tab is now writable: an Edit affordance is present.
    expect(screen.getByRole('button', { name: /Edit/ })).toBeInTheDocument()
  })

  it('opens the VM config editor and PUTs only the changed keys to the qemu config endpoint', async () => {
    mockApi.put.mockResolvedValue({ data: {} })
    renderModal('config')
    await screen.findByText('Disks')

    fireEvent.click(screen.getByRole('button', { name: /Edit/ }))
    // Change just the cores-per-socket value.
    const cores = await screen.findByPlaceholderText('e.g. 2')
    fireEvent.change(cores, { target: { value: '8' } })

    fireEvent.click(screen.getByRole('button', { name: /Review changes/ }))
    fireEvent.click(await screen.findByRole('button', { name: /Apply/ }))

    await waitFor(() =>
      expect(mockApi.put).toHaveBeenCalledWith(
        expect.stringContaining('/qemu/200/config'),
        expect.objectContaining({ cores: 8 }),
      ),
    )
    // Only the changed key is sent — memory / name / sockets are untouched.
    const body = mockApi.put.mock.calls[0][1]
    expect(body.memoryMib).toBeUndefined()
    expect(body.name).toBeUndefined()
    expect(body.sockets).toBeUndefined()
  })
})
