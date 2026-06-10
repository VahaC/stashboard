import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }))

import { api } from '@/lib/api'
import { LxcCreateModal } from './LxcCreateModal'
import type { ProxmoxConnection } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; post: any }

function connection(guests: { vmId: number }[] = []): ProxmoxConnection {
  return {
    id: 'c1', nodeName: 'pve', name: 'home', serverType: 'Pve', allowCreate: true,
    guests,
  } as unknown as ProxmoxConnection
}

function routeGet(url: string) {
  if (url.includes('/lxc/nextid')) return Promise.resolve({ data: { vmId: 150 } })
  if (url.includes('/lxc/templates'))
    return Promise.resolve({ data: [{ volid: 'local:vztmpl/debian-12.tar.zst', storage: 'local', size: 1 }] })
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
      <LxcCreateModal connection={conn} onClose={onClose} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.post.mockReset()
})

describe('LxcCreateModal — V6.13.1', () => {
  it('defaults the vmid from /cluster/nextid and POSTs the spec, then closes', async () => {
    const onClose = vi.fn()
    renderModal(connection(), onClose)

    // The vmid input is defaulted from the nextid query.
    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    // Need a password (or SSH key) before the form is valid.
    fireEvent.change(screen.getByPlaceholderText('write-only'), { target: { value: 'pw' } })

    fireEvent.click(screen.getByRole('button', { name: /Create container/ }))

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [url, spec] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/proxmox/connections/c1/lxc')
    expect(spec.vmId).toBe(150)
    expect(spec.osTemplate).toBe('local:vztmpl/debian-12.tar.zst')
    expect(spec.rootfsStorage).toBe('local-lvm')
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('blocks submit when the vmid is already in use on the host', async () => {
    renderModal(connection([{ vmId: 150 }]))

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.change(screen.getByPlaceholderText('write-only'), { target: { value: 'pw' } })

    expect(screen.getByText(/already in use/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Create container/ })).toBeDisabled()
    expect(mockApi.post).not.toHaveBeenCalled()
  })

  it('blocks submit on a malformed gateway', async () => {
    renderModal(connection())

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.change(screen.getByPlaceholderText('write-only'), { target: { value: 'pw' } })
    fireEvent.change(screen.getByPlaceholderText('10.0.0.1'), { target: { value: '999.1.1.1' } })

    expect(screen.getByRole('button', { name: /Create container/ })).toBeDisabled()
  })
})
