import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'

// The modal + its shared field children read only these query hooks.
vi.mock('@/lib/queries', () => ({
  useCreateComposeProject: vi.fn(),
  useComposeImageTags: vi.fn(),
  useComposeAllocation: vi.fn(),
  useServiceTemplates: vi.fn(),
}))

import {
  useComposeAllocation, useComposeImageTags, useCreateComposeProject, useServiceTemplates,
} from '@/lib/queries'
import { ComposeNewProjectModal } from './ComposeNewProjectModal'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockCreate = useCreateComposeProject as unknown as any
const mockImageTags = useComposeImageTags as unknown as any
const mockAllocation = useComposeAllocation as unknown as any
const mockTemplates = useServiceTemplates as unknown as any

function fillRequired() {
  fireEvent.change(screen.getByLabelText('Project name'), { target: { value: 'myapp' } })
  fireEvent.change(screen.getByLabelText('Directory'), { target: { value: '/opt/stacks' } })
  fireEvent.change(screen.getByLabelText('Service name'), { target: { value: 'web' } })
  fireEvent.change(screen.getByPlaceholderText('nginx:1.27'), { target: { value: 'nginx:1.27' } })
}

let mutateAsync: ReturnType<typeof vi.fn>
let onCreated: ReturnType<typeof vi.fn>

beforeEach(() => {
  mockCreate.mockReset()
  mockImageTags.mockReset()
  mockAllocation.mockReset()
  mockTemplates.mockReset()
  mutateAsync = vi.fn().mockResolvedValue({
    projectName: 'myapp', fileName: 'docker-compose.yml',
    directory: '/opt/stacks/myapp', started: true, startError: null,
  })
  onCreated = vi.fn()
  mockCreate.mockReturnValue({ mutateAsync, isPending: false })
  mockImageTags.mockReturnValue({ data: undefined, isFetching: false })
  mockAllocation.mockReturnValue({ data: undefined })
  mockTemplates.mockReturnValue({ data: [], isLoading: false, isError: false })
})

function renderModal() {
  return render(
    <ComposeNewProjectModal
      connectionId="c1"
      connectionName="home-server"
      onClose={() => {}}
      onCreated={onCreated}
    />,
  )
}

describe('ComposeNewProjectModal', () => {
  it('renders the project, directory, service and image fields', () => {
    renderModal()
    expect(screen.getByLabelText('Project name')).toBeInTheDocument()
    expect(screen.getByLabelText('Directory')).toBeInTheDocument()
    expect(screen.getByLabelText('Service name')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('nginx:1.27')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /create and run/i })).toBeInTheDocument()
    expect(screen.getByText(/home-server/)).toBeInTheDocument()
  })

  it('previews the project subfolder under the entered parent directory', () => {
    renderModal()
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: 'myapp' } })
    fireEvent.change(screen.getByLabelText('Directory'), { target: { value: '/opt/stacks' } })
    expect(screen.getByText('/opt/stacks/myapp/docker-compose.yml')).toBeInTheDocument()
  })

  it('keeps the actions disabled until name, directory, service and image are set', () => {
    renderModal()
    const run = screen.getByRole('button', { name: /create and run/i })
    expect(run).toBeDisabled()
    fillRequired()
    expect(run).toBeEnabled()
  })

  it('rejects an uppercase (invalid) project name', () => {
    renderModal()
    fireEvent.change(screen.getByLabelText('Project name'), { target: { value: 'MyApp' } })
    expect(screen.getByRole('alert')).toHaveTextContent(/lowercase/i)
  })

  it('offers no "Create only" action — creation always runs the stack', () => {
    renderModal()
    expect(screen.queryByRole('button', { name: /create only/i })).not.toBeInTheDocument()
  })

  it('always runs, posting the full payload with run: true', async () => {
    renderModal()
    fillRequired()
    fireEvent.click(screen.getByRole('button', { name: /create and run/i }))

    await vi.waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const arg = mutateAsync.mock.calls[0][0]
    expect(arg.projectName).toBe('myapp')
    // The entered directory is the parent; the project gets its own subfolder.
    expect(arg.directory).toBe('/opt/stacks/myapp')
    expect(arg.createDirectory).toBe(true)
    expect(arg.run).toBe(true)
    expect(arg.services[0].name).toBe('web')
    expect(arg.services[0].image).toBe('nginx:1.27')
  })

  it('switches to the "From template" tab and lists the catalogue', () => {
    mockTemplates.mockReturnValue({
      data: [{
        id: 'redis', name: 'Redis', description: 'In-memory store.',
        category: 'Databases', icon: 'redis', projectName: 'redis',
        variables: [], services: [{
          name: 'redis', image: 'redis:7', restart: null, ports: [], volumes: [],
          environment: [], labels: [], command: null, entrypoint: null,
          user: null, workingDir: null, resources: null,
        }], notes: null, documentation: null,
      }],
      isLoading: false, isError: false,
    })
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /from template/i }))
    expect(screen.getByText('Redis')).toBeInTheDocument()
    expect(screen.getByText('Databases')).toBeInTheDocument()
  })

  it('creates and runs, then notifies the parent', async () => {
    renderModal()
    fillRequired()
    fireEvent.click(screen.getByRole('button', { name: /create and run/i }))

    await vi.waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    expect(mutateAsync.mock.calls[0][0].run).toBe(true)
    await vi.waitFor(() => expect(onCreated).toHaveBeenCalledWith('myapp', true))
  })

  it('surfaces a run failure and keeps the modal open', async () => {
    mutateAsync.mockResolvedValueOnce({
      projectName: 'myapp', fileName: 'docker-compose.yml',
      directory: '/opt/stacks/myapp', started: false, startError: 'port is already allocated',
    })
    renderModal()
    fillRequired()
    fireEvent.click(screen.getByRole('button', { name: /create and run/i }))

    await vi.waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/already allocated/i))
    expect(onCreated).not.toHaveBeenCalled()
  })
})
