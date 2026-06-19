import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }))
// useFeatures (pulled in by sibling tabs) — keep it inert.
vi.mock('@/lib/queries', () => ({ useFeatures: () => ({ data: { allowProxmoxUpdates: false, allowProxmoxConsole: false } }), useServices: () => ({ data: [] }) }))

import { api } from '@/lib/api'
import { LxcModal } from './LxcModal'
import type { ProxmoxConnection, ProxmoxGuest, ProxmoxLxcDetail } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any }

const connection = { id: 'c1', nodeName: 'pve', name: 'home' } as unknown as ProxmoxConnection
const guest = { vmId: 101, name: 'wg', isRunning: true, tags: [] } as unknown as ProxmoxGuest

function detail(overrides: Partial<ProxmoxLxcDetail> = {}): ProxmoxLxcDetail {
  return {
    vmId: 101, status: 'running', cores: 2, memoryBytes: 2048 * 1024 * 1024, swapBytes: 512 * 1024 * 1024,
    hostname: 'wg', osType: 'debian', arch: 'amd64', onboot: true, unprivileged: true, features: null,
    networks: [{ key: 'net0', value: 'name=eth0,bridge=vmbr0,ip=dhcp' }],
    mounts: [
      { key: 'mp0', value: 'local-lvm:vm-101-disk-1,mp=/data,size=20G' },
      { key: 'rootfs', value: 'local-lvm:vm-101-disk-0,size=8G' },
    ],
    cpuFraction: 0.1, memUsedBytes: 100, memMaxBytes: 2048 * 1024 * 1024,
    diskUsedBytes: 1, diskMaxBytes: 8 * 1024 * 1024 * 1024, uptimeSeconds: 100,
    ...overrides,
  } as ProxmoxLxcDetail
}

function renderModal(d: ProxmoxLxcDetail) {
  mockApi.get.mockResolvedValue({ data: d })
  mockApi.put.mockResolvedValue({ data: null })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <LxcModal guest={guest} connection={connection} initialTab="config" onClose={() => {}} />
    </QueryClientProvider>,
  )
}

/** Enter edit mode from the read-only Config view. */
async function enterEdit() {
  fireEvent.click(await screen.findByRole('button', { name: /Edit/ }))
  await screen.findByText('Network interfaces')
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.put.mockReset()
  mockApi.post.mockReset()
})

describe('LxcModal Config editor — V6.9', () => {
  it('parses existing net / mp rows into structured fields', async () => {
    renderModal(detail())
    await enterEdit()

    expect((screen.getByDisplayValue('eth0') as HTMLInputElement).value).toBe('eth0')
    expect(screen.getByDisplayValue('vmbr0')).toBeInTheDocument()
    expect(screen.getByDisplayValue('/data')).toBeInTheDocument()
    expect(screen.getByDisplayValue('20G')).toBeInTheDocument()
  })

  it('builds an add → review entry for a new interface', async () => {
    renderModal(detail())
    await enterEdit()

    // The network section's "Add" is the first Add button on the form.
    fireEvent.click(screen.getAllByRole('button', { name: /Add/ })[0])
    fireEvent.click(screen.getByRole('button', { name: /Review changes/ }))

    expect(await screen.findByText(/Review \d+ change/)).toBeInTheDocument()
    expect(screen.getByText('New interface')).toBeInTheDocument()
  })

  it('names the exact key and the storage caveat when removing a mount', async () => {
    renderModal(detail())
    await enterEdit()

    // The mount section's Remove button (only net0 + mp0 exist; mp0 remove is 2nd Remove).
    const removes = screen.getAllByRole('button', { name: /Remove/ })
    fireEvent.click(removes[1])
    fireEvent.click(screen.getByRole('button', { name: /Review changes/ }))

    await screen.findByText(/Review \d+ change/)
    expect(screen.getByText('Destructive changes:')).toBeInTheDocument()
    expect(screen.getByText(/mp0 will be removed/)).toBeInTheDocument()
    expect(screen.getByText(/storage content is NOT deleted/)).toBeInTheDocument()
  })

  it('surfaces the advanced/preserved note when a line carries unmodelled options', async () => {
    renderModal(detail({ networks: [{ key: 'net0', value: 'name=eth0,bridge=vmbr0,ip=dhcp,trunks=2-4' }] }))
    await enterEdit()
    expect(await screen.findByText(/Options Stashboard doesn't model are preserved/)).toBeInTheDocument()
    expect(screen.getByText('trunks=2-4')).toBeInTheDocument()
  })

  it('sends a net upsert on apply', async () => {
    renderModal(detail())
    await enterEdit()

    fireEvent.change(screen.getByDisplayValue('eth0'), { target: { value: 'eth1' } })
    fireEvent.click(screen.getByRole('button', { name: /Review changes/ }))
    fireEvent.click(await screen.findByRole('button', { name: /Apply/ }))

    await waitFor(() => expect(mockApi.put).toHaveBeenCalledTimes(1))
    const [, body] = mockApi.put.mock.calls[0]
    expect(body.networks).toHaveLength(1)
    expect(body.networks[0].key).toBe('net0')
    expect(body.networks[0].name).toBe('eth1')
    // Saved state surfaces restart guidance for a running guest.
    expect(await screen.findByText(/restart/)).toBeInTheDocument()
  })

  it('regression: a V6.5 scalar edit still works alongside the structured editor', async () => {
    renderModal(detail())
    await enterEdit()

    fireEvent.change(screen.getByDisplayValue('2'), { target: { value: '4' } })  // cores
    fireEvent.click(screen.getByRole('button', { name: /Review changes/ }))
    fireEvent.click(await screen.findByRole('button', { name: /Apply/ }))

    await waitFor(() => expect(mockApi.put).toHaveBeenCalledTimes(1))
    expect(mockApi.put.mock.calls[0][1].cores).toBe(4)
  })

  it('blocks review when a static IP is malformed', async () => {
    renderModal(detail())
    await enterEdit()

    fireEvent.change(screen.getByDisplayValue('dhcp'), { target: { value: '192.168.1.5' } })  // no prefix
    expect(await screen.findByText(/not dhcp, manual, or a valid IPv4 CIDR/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Review changes/ })).toBeDisabled()
  })
})
