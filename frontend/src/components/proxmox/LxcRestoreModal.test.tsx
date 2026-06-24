import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn(), delete: vi.fn() } }))

import { api } from '@/lib/api'
import { LxcRestoreModal } from './LxcRestoreModal'
import type { ProxmoxConnection } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; post: any }

type Guest = { vmId: number; name: string; isRunning: boolean; guestType: string }

function connection(guests: Guest[] = []): ProxmoxConnection {
  return {
    id: 'c1', nodeName: 'pve', name: 'home', serverType: 'Pve', allowRestore: true,
    guests,
  } as unknown as ProxmoxConnection
}

const BACKUP = 'local:backup/vzdump-lxc-101-2026_01_01-00_00_00.tar.zst'
const VM_BACKUP = 'local:backup/vzdump-qemu-200-2026_01_01-00_00_00.vma.zst'

function routeGet(url: string) {
  if (url.includes('/lxc/nextid')) return Promise.resolve({ data: { vmId: 150 } })
  if (url.includes('/lxc/backups'))
    return Promise.resolve({ data: [{ volid: BACKUP, storage: 'local', vmId: 101, cTime: 1767225600, size: 12345, format: 'tar.zst', notes: null }] })
  if (url.includes('/qemu/backups'))
    return Promise.resolve({ data: [{ volid: VM_BACKUP, storage: 'local', vmId: 200, cTime: 1767225600, size: 999, format: 'vma.zst', notes: null }] })
  if (url.includes('/node/storage'))
    return Promise.resolve({ data: [{ storage: 'local-lvm', content: 'rootdir,images', enabled: true, active: true }] })
  return Promise.resolve({ data: [] })
}

function renderModal(conn: ProxmoxConnection, onClose = () => {}, isVm = false) {
  mockApi.get.mockImplementation((url: string) => routeGet(url))
  mockApi.post.mockResolvedValue({ data: conn })
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <LxcRestoreModal connection={conn} isVm={isVm} onClose={onClose} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.post.mockReset()
})

describe('LxcRestoreModal — V8.1', () => {
  it('defaults the vmid + backup and POSTs a new-vmid restore, then closes', async () => {
    const onClose = vi.fn()
    renderModal(connection(), onClose)

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    // The backup dropdown is populated from /lxc/backups.
    await waitFor(() => expect(screen.getByDisplayValue(/CT 101/)).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: /Restore container/ }))

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [url, spec] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/proxmox/connections/c1/lxc/restore')
    expect(spec.vmId).toBe(150)
    expect(spec.backupVolid).toBe(BACKUP)
    expect(spec.force).toBe(false)
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('blocks a new-vmid restore when the target already exists', async () => {
    renderModal(connection([{ vmId: 150, name: 'web', isRunning: false, guestType: 'Lxc' }]))

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())

    expect(screen.getByText(/already in use/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Restore container/ })).toBeDisabled()
    expect(mockApi.post).not.toHaveBeenCalled()
  })

  it('refuses an overwrite over a running target', async () => {
    renderModal(connection([{ vmId: 150, name: 'web', isRunning: true, guestType: 'Lxc' }]))

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText(/Overwrite existing container/))

    expect(screen.getByText(/Stop CT 150 before restoring/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Restore container/ })).toBeDisabled()
  })

  it('double-confirms an overwrite over a stopped target, then POSTs force', async () => {
    const onClose = vi.fn()
    renderModal(connection([{ vmId: 150, name: 'web', isRunning: false, guestType: 'Lxc' }]), onClose)

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText(/Overwrite existing container/))

    // First click opens the confirm dialog (no POST yet).
    fireEvent.click(screen.getByRole('button', { name: /Restore container/ }))
    expect(mockApi.post).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.getByText('Overwrite container?')).toBeInTheDocument())

    // Confirming fires the restore with force=true.
    fireEvent.click(screen.getByRole('button', { name: /Overwrite CT 150/ }))
    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [, spec] = mockApi.post.mock.calls[0]
    expect(spec.force).toBe(true)
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })
})

describe('LxcRestoreModal — V8.3 (VM)', () => {
  it('lists vzdump-qemu archives and POSTs to the qemu restore endpoint', async () => {
    const onClose = vi.fn()
    renderModal(connection(), onClose, true)

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    // The archive dropdown is populated from /qemu/backups and labelled "VM …".
    await waitFor(() => expect(screen.getByDisplayValue(/VM 200/)).toBeInTheDocument())
    // The LXC-only "Unprivileged container" option is hidden for a VM.
    expect(screen.queryByLabelText(/Unprivileged/)).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /Restore VM/ }))

    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [url, spec] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/proxmox/connections/c1/qemu/restore')
    expect(spec.vmId).toBe(150)
    expect(spec.backupVolid).toBe(VM_BACKUP)
    expect(spec.force).toBe(false)
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('double-confirms an overwrite over a stopped VM, then POSTs force', async () => {
    const onClose = vi.fn()
    renderModal(connection([{ vmId: 150, name: 'web', isRunning: false, guestType: 'Qemu' }]), onClose, true)

    await waitFor(() => expect(screen.getByDisplayValue('150')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText(/Overwrite existing VM/))

    fireEvent.click(screen.getByRole('button', { name: /Restore VM/ }))
    expect(mockApi.post).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.getByText('Overwrite VM?')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: /Overwrite VM 150/ }))
    await waitFor(() => expect(mockApi.post).toHaveBeenCalled())
    const [, spec] = mockApi.post.mock.calls[0]
    expect(spec.force).toBe(true)
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })
})
