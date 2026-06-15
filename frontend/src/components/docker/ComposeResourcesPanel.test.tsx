import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import type { ComposeProject } from '@/lib/types'

vi.mock('@/lib/queries', () => ({
  useComposeHostNetworks: vi.fn(),
  useComposeVolumeUsage: vi.fn(),
  useEditComposeResource: vi.fn(),
  useDeleteComposeResource: vi.fn(),
}))

import {
  useComposeHostNetworks,
  useComposeVolumeUsage,
  useDeleteComposeResource,
  useEditComposeResource,
} from '@/lib/queries'
import { ComposeResourcesPanel } from './ComposeResourcesPanel'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockHostNetworks = useComposeHostNetworks as unknown as any
const mockVolumeUsage = useComposeVolumeUsage as unknown as any
const mockEdit = useEditComposeResource as unknown as any
const mockDelete = useDeleteComposeResource as unknown as any

const PROJECT: ComposeProject = {
  projectName: 'homelab',
  fileName: 'docker-compose.yml',
  projectPath: '/opt/stacks/homelab',
  services: [],
  networks: [
    { name: 'frontend', external: false, nameOverride: null, driver: 'bridge', subnet: '172.20.0.0/24', gateway: '172.20.0.1', driverOpts: [] },
    { name: 'proxy', external: true, nameOverride: 'edge_net', driver: null, subnet: null, gateway: null, driverOpts: [] },
  ],
  volumes: [
    { name: 'data', external: false, nameOverride: null, driver: null, driverOpts: [] },
  ],
  secrets: [],
  configs: [],
  unsupportedFeatures: [],
}

let mutateEdit: ReturnType<typeof vi.fn>
let mutateDelete: ReturnType<typeof vi.fn>

beforeEach(() => {
  mutateEdit = vi.fn().mockResolvedValue({ changed: true, project: PROJECT })
  mutateDelete = vi.fn().mockResolvedValue({ changed: true, project: PROJECT })
  mockHostNetworks.mockReturnValue({ data: [] })
  mockVolumeUsage.mockReturnValue({ data: [] })
  mockEdit.mockReturnValue({ mutateAsync: mutateEdit, isPending: false })
  mockDelete.mockReturnValue({ mutateAsync: mutateDelete, isPending: false })
})

function renderPanel(kind: 'networks' | 'volumes' | 'secrets' | 'configs', readOnly = false) {
  return render(
    <ComposeResourcesPanel
      connectionId="c1"
      project="homelab"
      projectData={PROJECT}
      kind={kind}
      readOnly={readOnly}
    />,
  )
}

describe('ComposeResourcesPanel — networks', () => {
  it('lists network entries with their options and the external badge', () => {
    renderPanel('networks')
    expect(screen.getByText('frontend')).toBeInTheDocument()
    expect(screen.getByText('172.20.0.0/24')).toBeInTheDocument()
    expect(screen.getByText('proxy')).toBeInTheDocument()
    expect(screen.getByText('external')).toBeInTheDocument()
  })

  it('warns when a new subnet overlaps a host network', () => {
    mockHostNetworks.mockReturnValue({ data: [{ name: 'br-lan', driver: 'bridge', subnets: ['172.20.0.0/16'] }] })
    renderPanel('networks')
    fireEvent.click(screen.getByRole('button', { name: /add network/i }))
    fireEvent.change(screen.getByPlaceholderText('172.20.0.0/24'), { target: { value: '172.20.5.0/24' } })
    expect(screen.getByRole('alert')).toHaveTextContent('Subnet overlaps host network')
    expect(screen.getByRole('alert')).toHaveTextContent('br-lan')
  })

  it('saves a new network through the edit mutation', async () => {
    renderPanel('networks')
    fireEvent.click(screen.getByRole('button', { name: /add network/i }))
    fireEvent.change(screen.getByPlaceholderText('frontend'), { target: { value: 'backend' } })
    fireEvent.change(screen.getByPlaceholderText('bridge'), { target: { value: 'bridge' } })
    fireEvent.click(screen.getByRole('button', { name: /^Add network$/i }))

    await vi.waitFor(() => expect(mutateEdit).toHaveBeenCalledTimes(1))
    const call = mutateEdit.mock.calls[0][0]
    expect(call.kind).toBe('networks')
    expect(call.name).toBe('backend')
    expect(call.data.driver).toBe('bridge')
    expect(call.data.external).toBe(false)
  })

  it('rejects a duplicate network name', () => {
    renderPanel('networks')
    fireEvent.click(screen.getByRole('button', { name: /add network/i }))
    fireEvent.change(screen.getByPlaceholderText('frontend'), { target: { value: 'frontend' } })
    expect(screen.getByRole('alert')).toHaveTextContent('already exists')
  })

  it('hides edit/add controls when read-only', () => {
    renderPanel('networks', true)
    expect(screen.queryByRole('button', { name: /add network/i })).not.toBeInTheDocument()
  })
})

describe('ComposeResourcesPanel — volumes', () => {
  it('shows the on-disk size matched by the project-prefixed volume name', () => {
    mockVolumeUsage.mockReturnValue({
      data: [{ name: 'homelab_data', sizeBytes: 4_509_715_660, refCount: 1 }],
    })
    renderPanel('volumes')
    expect(screen.getByText(/GiB/)).toBeInTheDocument()
  })

  it('confirms before deleting an entry', async () => {
    renderPanel('volumes')
    fireEvent.click(screen.getByTitle('Delete data'))
    fireEvent.click(screen.getByRole('button', { name: /confirm delete/i }))
    await vi.waitFor(() => expect(mutateDelete).toHaveBeenCalledTimes(1))
    expect(mutateDelete.mock.calls[0][0]).toEqual({ kind: 'volumes', name: 'data' })
  })
})

describe('ComposeResourcesPanel — secrets', () => {
  it('requires a file path for a non-external secret', () => {
    renderPanel('secrets')
    fireEvent.click(screen.getByRole('button', { name: /add secret/i }))
    fireEvent.change(screen.getByPlaceholderText('db_password'), { target: { value: 'db_password' } })
    expect(screen.getByRole('button', { name: /^Add secret$/i })).toBeDisabled()
    fireEvent.change(screen.getByPlaceholderText('./secrets/db_password.txt'), {
      target: { value: './secrets/db.txt' },
    })
    expect(screen.getByRole('button', { name: /^Add secret$/i })).not.toBeDisabled()
  })
})
