import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'

vi.mock('@/lib/queries', () => ({
  useServiceTemplates: vi.fn(),
  useCreateComposeProject: vi.fn(),
}))

import { useCreateComposeProject, useServiceTemplates } from '@/lib/queries'
import type { ServiceTemplate } from '@/lib/types'
import { ComposeTemplateTab } from './ComposeTemplateTab'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockTemplates = useServiceTemplates as unknown as any
const mockCreate = useCreateComposeProject as unknown as any

const postgres: ServiceTemplate = {
  id: 'postgres', name: 'PostgreSQL', description: 'Relational database.',
  category: 'Databases', icon: 'postgresql', projectName: 'postgres',
  notes: 'Connect with the credentials above.', documentation: 'https://example.test',
  variables: [
    { key: 'PW', label: 'Password', type: 'password', hint: 'Superuser password', default: null, required: true, generate: true },
    { key: 'PORT', label: 'Port', type: 'port', hint: null, default: '5432', required: false, generate: false },
  ],
  services: [{
    name: 'db', image: 'postgres:17', restart: 'unless-stopped',
    ports: ['${PORT}:5432'], volumes: ['./data:/var/lib/postgresql/data'],
    environment: [{ name: 'POSTGRES_PASSWORD', value: '${PW}' }],
    labels: [], command: null, entrypoint: null, user: null, workingDir: null,
    resources: null as never,
  }],
}

let mutateAsync: ReturnType<typeof vi.fn>
let onCreated: ReturnType<typeof vi.fn>

beforeEach(() => {
  mockTemplates.mockReset()
  mockCreate.mockReset()
  mutateAsync = vi.fn().mockResolvedValue({
    projectName: 'postgres', fileName: 'docker-compose.yml',
    directory: '/opt/stacks/pg', started: true, startError: null,
  })
  onCreated = vi.fn()
  mockCreate.mockReturnValue({ mutateAsync, isPending: false })
  mockTemplates.mockReturnValue({ data: [postgres], isLoading: false, isError: false })
})

function renderTab() {
  return render(<ComposeTemplateTab connectionId="c1" onCreated={onCreated} />)
}

describe('ComposeTemplateTab', () => {
  it('renders a card per template and opens the config on click', () => {
    renderTab()
    fireEvent.click(screen.getByText('PostgreSQL'))
    // Project name is prefilled from the template.
    expect(screen.getByLabelText('Project name')).toHaveValue('postgres')
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
    expect(screen.getByText(/Connect with the credentials/)).toBeInTheDocument()
  })

  it('keeps actions disabled until directory and required variables are set', () => {
    renderTab()
    fireEvent.click(screen.getByText('PostgreSQL'))
    const run = screen.getByRole('button', { name: /create and run/i })
    expect(run).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Directory'), { target: { value: '/opt/stacks/pg' } })
    expect(run).toBeDisabled() // required password still blank
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 's3cret' } })
    expect(run).toBeEnabled()
  })

  it('posts the resolved, placeholder-substituted request', async () => {
    renderTab()
    fireEvent.click(screen.getByText('PostgreSQL'))
    fireEvent.change(screen.getByLabelText('Directory'), { target: { value: '/opt/stacks/pg' } })
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 's3cret' } })
    fireEvent.click(screen.getByRole('button', { name: /create and run/i }))

    await vi.waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const arg = mutateAsync.mock.calls[0][0]
    expect(arg.projectName).toBe('postgres')
    expect(arg.directory).toBe('/opt/stacks/pg')
    expect(arg.run).toBe(true)
    expect(arg.services[0].ports).toEqual(['5432:5432'])
    expect(arg.services[0].environment[0].value).toBe('s3cret')
    await vi.waitFor(() => expect(onCreated).toHaveBeenCalledWith('postgres', true))
  })

  it('can go back to the grid from the config panel', () => {
    renderTab()
    fireEvent.click(screen.getByText('PostgreSQL'))
    fireEvent.click(screen.getByRole('button', { name: /templates/i }))
    // The search box is part of the grid view only.
    expect(screen.getByLabelText('Search templates')).toBeInTheDocument()
  })
})
