import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import type { ComposeProject, ComposeService } from '@/lib/types'

// Mock the query layer so the modal renders without a server. It reads
// `.data` / `.isLoading` / `.error`; the edit mutation additionally exposes
// `mutateAsync` / `isPending`.
vi.mock('@/lib/queries', () => ({
  useComposeProject: vi.fn(),
  useComposeImageTags: vi.fn(),
  useEditComposeService: vi.fn(),
  useDockerConnection: vi.fn(),
  useDockerInstanceContainers: vi.fn(),
  useComposeAllocation: vi.fn(),
  useComposeHostNetworks: vi.fn(),
  useComposeVolumeUsage: vi.fn(),
  useEditComposeResource: vi.fn(),
  useDeleteComposeResource: vi.fn(),
  useCreateComposeService: vi.fn(),
  useComposeUp: vi.fn(),
  useComposeFile: vi.fn(),
  useSaveComposeFile: vi.fn(),
}))

import {
  useComposeAllocation,
  useComposeFile,
  useComposeHostNetworks,
  useComposeImageTags,
  useComposeProject,
  useComposeUp,
  useComposeVolumeUsage,
  useCreateComposeService,
  useDeleteComposeResource,
  useDockerConnection,
  useDockerInstanceContainers,
  useEditComposeResource,
  useEditComposeService,
  useSaveComposeFile,
} from '@/lib/queries'
import { ComposeProjectModal } from './ComposeProjectModal'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockCompose = useComposeProject as unknown as any
const mockImageTags = useComposeImageTags as unknown as any
const mockEdit = useEditComposeService as unknown as any
const mockConnection = useDockerConnection as unknown as any
const mockContainers = useDockerInstanceContainers as unknown as any
const mockAllocation = useComposeAllocation as unknown as any
const mockHostNetworks = useComposeHostNetworks as unknown as any
const mockVolumeUsage = useComposeVolumeUsage as unknown as any
const mockEditResource = useEditComposeResource as unknown as any
const mockDeleteResource = useDeleteComposeResource as unknown as any
const mockCreate = useCreateComposeService as unknown as any
const mockUp = useComposeUp as unknown as any
const mockFile = useComposeFile as unknown as any
const mockSaveFile = useSaveComposeFile as unknown as any

const SERVICE: ComposeService = {
  name: 'web',
  image: 'nginx:1.27',
  containerName: null,
  restart: 'unless-stopped',
  ports: ['8080:80'],
  volumes: ['./conf:/etc/nginx:ro'],
  environment: [{ name: 'TZ', value: 'Europe/Kyiv' }],
  envFiles: [],
  dependsOn: ['db'],
  networks: ['frontend'],
  resources: {
    convention: 'deploy',
    cpuLimit: '0.50', cpuReservation: null,
    memLimit: '512M', memReservation: null,
    pidsLimit: null,
    cpuShares: null, oomKillDisable: null, oomScoreAdj: null, shmSize: null,
    ulimits: [],
  },
  labels: [{ name: 'com.example.team', value: 'media' }],
  command: 'nginx -g "daemon off;"',
  entrypoint: null,
  user: null,
  workingDir: null,
}

const PROJECT: ComposeProject = {
  projectName: 'homelab',
  fileName: 'docker-compose.yml',
  projectPath: '/opt/stacks/homelab',
  services: [SERVICE, {
    ...SERVICE, name: 'db', image: 'mariadb:11', ports: [], restart: null,
    labels: [], command: null,
  }],
  networks: [{ name: 'frontend', external: false, nameOverride: null, driver: 'bridge', subnet: null, gateway: null, driverOpts: [] }],
  volumes: [{ name: 'data', external: false, nameOverride: null, driver: null, driverOpts: [] }],
  secrets: [],
  configs: [],
  unsupportedFeatures: [],
}

function renderModal() {
  return render(<ComposeProjectModal connectionId="c1" project="homelab" onClose={() => {}} />)
}

beforeEach(() => {
  mockCompose.mockReset()
  mockImageTags.mockReset()
  mockEdit.mockReset()
  mockConnection.mockReset()
  mockContainers.mockReset()
  mockConnection.mockReturnValue({ data: { id: 'c1', name: 'home-server' }, isLoading: false })
  mockContainers.mockReturnValue({ data: [] })
  mockAllocation.mockReturnValue({ data: { containerCount: 0, reservedCpus: 0, reservedMemoryBytes: 0 } })
  mockCompose.mockReturnValue({ data: PROJECT, isLoading: false, error: null })
  mockImageTags.mockReturnValue({ data: undefined, isFetching: false })
  mockEdit.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ changed: true, project: PROJECT }), isPending: false })
  mockHostNetworks.mockReturnValue({ data: [] })
  mockVolumeUsage.mockReturnValue({ data: [] })
  mockEditResource.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ changed: true, project: PROJECT }), isPending: false })
  mockDeleteResource.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ changed: true, project: PROJECT }), isPending: false })
  mockCreate.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ changed: true, project: PROJECT }), isPending: false })
  mockUp.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ success: true, output: 'ok', error: null }), isPending: false })
  mockFile.mockReturnValue({ data: { fileName: 'docker-compose.yml', projectPath: '/opt/stacks/homelab', content: 'services:\n  web:\n    image: nginx:1.27\n' }, isLoading: false, error: null })
  mockSaveFile.mockReturnValue({ mutateAsync: vi.fn().mockResolvedValue({ changed: true }), isPending: false })
})

// ── Header strip + tabs ────────────────────────────────────────────────────

describe('ComposeProjectModal — layout', () => {
  it('renders one tab per container plus the shared resources tab', () => {
    renderModal()
    // A tab per container (compose service name) — not labelled "service".
    expect(screen.getByRole('tab', { name: /web/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /db/ })).toBeInTheDocument()
    // V7.3 — the shared settings live behind their own top-level tab.
    expect(screen.getByRole('tab', { name: /Shared resources/ })).toBeInTheDocument()
    // The resource sub-tabs are not shown until that tab is opened.
    expect(screen.queryByRole('tab', { name: /Networks/ })).not.toBeInTheDocument()
    // Compact header: file name + project name + connection.
    expect(screen.getByText(/docker-compose\.yml/)).toBeInTheDocument()
    expect(screen.getByText('homelab')).toBeInTheDocument()
    expect(screen.getByText(/home-server/)).toBeInTheDocument()
  })

  it('opens shared resources with the four sections and an explanation', () => {
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /Shared resources/ }))
    expect(screen.getByRole('tab', { name: /Networks/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /Volumes/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /Secrets/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /Configs/ })).toBeInTheDocument()
    // Each section explains itself; it lands on the first non-empty one
    // (networks, in this fixture) and shows that section's blurb.
    expect(screen.getByText(/reach each other/i)).toBeInTheDocument()
    // Switching to Volumes swaps the explanation.
    fireEvent.click(screen.getByRole('tab', { name: /Volumes/ }))
    expect(screen.getByText(/survives a container being recreated/i)).toBeInTheDocument()
  })

  it('defaults to the first container tab and shows its editable image', () => {
    renderModal()
    expect(screen.getByDisplayValue('nginx:1.27')).toBeInTheDocument()
    // The read-only "More" block carries the non-editable fields.
    expect(screen.getByText('Depends on')).toBeInTheDocument()
    // V7.2 — resource constraints get their own editor section, not the
    // read-only "More" block.
    expect(screen.getByText('Resource constraints')).toBeInTheDocument()
  })

  it('switches tabs to edit a different service', () => {
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /db/ }))
    expect(screen.getByDisplayValue('mariadb:11')).toBeInTheDocument()
  })

  it('wears the live container state when a compose-service label matches', () => {
    mockContainers.mockReturnValue({
      data: [{
        id: 'abc', name: 'homelab-web-1', image: 'nginx:1.27', state: 'running',
        composeProject: 'homelab', composeService: 'web', ports: [],
      }],
    })
    renderModal()
    expect(screen.getAllByText('running').length).toBeGreaterThan(0)
    // The undeployed db service falls back to the "not deployed" pill.
    expect(screen.getAllByText('not deployed').length).toBeGreaterThan(0)
  })
})

// ── Editing ────────────────────────────────────────────────────────────────

describe('ComposeProjectModal — editing', () => {
  it('reverts unsaved edits when Cancel is clicked', () => {
    renderModal()
    fireEvent.change(screen.getByDisplayValue('nginx:1.27'), { target: { value: 'nginx:1.99' } })
    expect(screen.getByDisplayValue('nginx:1.99')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /^cancel$/i }))
    expect(screen.getByDisplayValue('nginx:1.27')).toBeInTheDocument()
  })

  it('saves the desired state through the edit mutation', async () => {
    const mutateAsync = vi.fn().mockResolvedValue({ changed: true, project: PROJECT })
    mockEdit.mockReturnValue({ mutateAsync, isPending: false })
    renderModal()

    fireEvent.change(screen.getByDisplayValue('nginx:1.27'), { target: { value: 'nginx:1.28' } })
    fireEvent.click(screen.getByRole('button', { name: /save changes/i }))

    await vi.waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const call = mutateAsync.mock.calls[0][0]
    expect(call.serviceName).toBe('web')
    expect(call.data.image).toBe('nginx:1.28')
    // Send-back-what-you-got: untouched fields carry the current state.
    expect(call.data.ports).toEqual(['8080:80'])
    expect(call.data.environment).toEqual([{ name: 'TZ', value: 'Europe/Kyiv' }])
  })

  it('warns about a host-port collision with another service', () => {
    const projectWithClash: ComposeProject = {
      ...PROJECT,
      services: [PROJECT.services[0], { ...PROJECT.services[1], ports: ['9090:3306'] }],
    }
    mockCompose.mockReturnValue({ data: projectWithClash, isLoading: false, error: null })
    renderModal()

    fireEvent.change(screen.getByLabelText('Host port 1'), { target: { value: '9090' } })
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Host port 9090 is already published by service "db".',
    )
  })

  it('shows the read-only banner and hides the editor when the file uses unsupported features', () => {
    mockCompose.mockReturnValue({
      data: { ...PROJECT, unsupportedFeatures: ["YAML merge key (<<) in service 'web'"] },
      isLoading: false,
      error: null,
    })
    renderModal()
    expect(screen.getByRole('status')).toHaveTextContent(
      "file uses YAML merge key (<<) in service 'web'",
    )
    // No editable form in read-only mode — details only.
    expect(screen.queryByRole('button', { name: /save changes/i })).not.toBeInTheDocument()
  })

  it('surfaces the API error envelope when the project cannot be loaded', () => {
    mockCompose.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: { response: { data: { error: "No containers with compose project 'homelab' were found on this connection." } } },
    })
    renderModal()
    expect(screen.getByText(/No containers with compose project/)).toBeInTheDocument()
  })
})

// ── V7.4 — Add service + Raw YAML tabs ───────────────────────────────────────

describe('ComposeProjectModal — V7.4 create + raw tabs', () => {
  it('renders the Add service and Raw YAML tabs', () => {
    renderModal()
    expect(screen.getByRole('tab', { name: /Add service/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /Raw YAML/ })).toBeInTheDocument()
  })

  it('hides the Add service tab in read-only mode but keeps Raw YAML', () => {
    mockCompose.mockReturnValue({
      data: { ...PROJECT, unsupportedFeatures: ['extends'] },
      isLoading: false,
      error: null,
    })
    renderModal()
    expect(screen.queryByRole('tab', { name: /Add service/ })).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /Raw YAML/ })).toBeInTheDocument()
  })

  it('the Add service tab exposes a name field, an image field and a Save and run button', () => {
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /Add service/ }))
    expect(screen.getByLabelText('Service name')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /save and run/i })).toBeInTheDocument()
  })

  it('creates a new service then brings the project up on Save and run', async () => {
    const create = vi.fn().mockResolvedValue({ changed: true, project: PROJECT })
    const up = vi.fn().mockResolvedValue({ success: true, output: 'ok', error: null })
    mockCreate.mockReturnValue({ mutateAsync: create, isPending: false })
    mockUp.mockReturnValue({ mutateAsync: up, isPending: false })
    renderModal()

    fireEvent.click(screen.getByRole('tab', { name: /Add service/ }))
    fireEvent.change(screen.getByLabelText('Service name'), { target: { value: 'cache' } })
    fireEvent.change(screen.getByPlaceholderText('nginx:1.27'), { target: { value: 'redis:7' } }) // image field
    expect(screen.getByDisplayValue('redis:7')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /save and run/i }))
    await vi.waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create.mock.calls[0][0].name).toBe('cache')
    expect(create.mock.calls[0][0].image).toBe('redis:7')
    await vi.waitFor(() => expect(up).toHaveBeenCalledTimes(1))
  })

  it('rejects a duplicate service name before saving', () => {
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /Add service/ }))
    fireEvent.change(screen.getByLabelText('Service name'), { target: { value: 'web' } })
    expect(screen.getByRole('alert')).toHaveTextContent(/already exists/i)
  })

  it('shows the raw file text and saves edited content', async () => {
    const save = vi.fn().mockResolvedValue({ changed: true })
    mockSaveFile.mockReturnValue({ mutateAsync: save, isPending: false })
    renderModal()

    fireEvent.click(screen.getByRole('tab', { name: /Raw YAML/ }))
    const textarea = screen.getByLabelText('Raw Compose file')
    expect(textarea).toHaveValue('services:\n  web:\n    image: nginx:1.27\n')

    fireEvent.change(textarea, { target: { value: 'services:\n  web:\n    image: nginx:1.28\n' } })
    fireEvent.click(screen.getByRole('button', { name: /save only/i }))
    await vi.waitFor(() => expect(save).toHaveBeenCalledTimes(1))
    expect(save.mock.calls[0][0]).toContain('nginx:1.28')
  })
})
