import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }))

import { api } from '@/lib/api'
import { QemuCreateModal } from './QemuCreateModal'
import type { ProxmoxConnection } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; post: any }

const ISO = 'local:iso/debian-12.iso'

function connection(guests: { vmId: number }[] = []): ProxmoxConnection {
  return {
    id: 'c1', nodeName: 'pve', name: 'home', serverType: 'Pve', allowCreate: true,
    guests,
  } as unknown as ProxmoxConnection
}

function routeGet(url: string) {
  if (url.includes('/lxc/nextid')) return Promise.resolve({ data: { vmId: 150 } })
  if (url.includes('/qemu/isos'))
    return Promise.resolve({ data: [{ volid: ISO, storage: 'local', size: 700_000_000 }] })
  if (url.includes('/node/storage'))
    return Promise.resolve({ data: [{ storage: 'local-lvm', content: 'rootdir,images', enabled: true, active: true }] })
  return Promise.resolve({ data: [] })
}

function renderModal(conn: ProxmoxConnection, onClose = () => {}) {
  mockApi.get.mockImplementation((url: string) => routeGet(url))
  mockApi.post.mockResolvedValue({ data: conn })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <QemuCreateModal connection={conn} onClose={onClose} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.post.mockReset()
})

describe('QemuCreateModal — V8.4', () => {
  it('defaults the vmid + disk storage + ISO and POSTs the spec to /qemu, then closes', async () => {
    const onClose = vi.fn()
    renderModal(connection(), onClose)

    // The vmid input is defaulted from the nextid query (shared with LXC create).
    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    // The ISO dropdown is populated from /qemu/isos.
    await waitFor(() => expect(screen.getByDisplayValue(ISO)).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: /Create VM/ }))

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [url, spec] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/proxmox/connections/c1/qemu')
    expect(spec.vmId).toBe(150)
    expect(spec.diskStorage).toBe('local-lvm')
    expect(spec.isoVolid).toBe(ISO)
    expect(spec.bios).toBe('seabios')
    expect(spec.osType).toBe('l26')
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('blocks submit when the vmid is already in use on the host', async () => {
    renderModal(connection([{ vmId: 150 }]))

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())

    expect(screen.getByText(/already in use/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Create VM/ })).toBeDisabled()
    expect(mockApi.post).not.toHaveBeenCalled()
  })

  it('blocks submit on a malformed MAC address', async () => {
    renderModal(connection())

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.change(screen.getByPlaceholderText('aa:bb:cc:dd:ee:ff'), { target: { value: 'zz:zz' } })

    expect(screen.getByRole('button', { name: /Create VM/ })).toBeDisabled()
  })
})
