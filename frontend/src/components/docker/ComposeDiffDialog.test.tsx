import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import type { ComposeFileDiff } from '@/lib/types'
import { ComposeDiffDialog } from './ComposeDiffDialog'

/** V7.6 — the presentational diff/dry-run confirm dialog: validation banner,
 *  changed/removed service chips, the unified diff, and the unchanged state. */

const base: ComposeFileDiff = {
  fileName: 'docker-compose.yml',
  projectPath: '/opt/stacks/media',
  unchanged: false,
  diff: [
    { type: 'Context', text: 'services:', oldLine: 1, newLine: 1 },
    { type: 'Removed', text: '    image: nginx:1.27', oldLine: 2, newLine: null },
    { type: 'Added', text: '    image: nginx:1.28', oldLine: null, newLine: 2 },
  ],
  valid: true,
  validationError: null,
  cliAvailable: true,
  changedServices: ['web'],
  removedServices: ['old'],
}

describe('ComposeDiffDialog', () => {
  it('shows the valid verdict, changed + removed services and the diff', () => {
    render(
      <ComposeDiffDialog open onClose={() => {}} title="Review changes" diff={base}
        isLoading={false} error={null} actions={<button>Save only</button>} />,
    )
    expect(screen.getByText(/Validates with/i)).toBeInTheDocument()
    expect(screen.getByText('web')).toBeInTheDocument()
    expect(screen.getByText('old')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Diff' })).toHaveTextContent('nginx:1.28')
    expect(screen.getByRole('button', { name: 'Save only' })).toBeInTheDocument()
  })

  it('surfaces the validation error when the proposed file is invalid', () => {
    render(
      <ComposeDiffDialog open onClose={() => {}} title="Review changes"
        diff={{ ...base, valid: false, validationError: 'yaml: line 3: bad indent' }}
        isLoading={false} error={null} actions={null} />,
    )
    expect(screen.getByRole('alert')).toHaveTextContent('bad indent')
  })

  it('reports the no-CLI case', () => {
    render(
      <ComposeDiffDialog open onClose={() => {}} title="Review changes"
        diff={{ ...base, cliAvailable: false, valid: false }}
        isLoading={false} error={null} actions={null} />,
    )
    expect(screen.getByRole('status')).toHaveTextContent(/CLI is available to validate/i)
  })

  it('shows the unchanged message when nothing differs', () => {
    render(
      <ComposeDiffDialog open onClose={() => {}} title="Review changes"
        diff={{ ...base, unchanged: true, diff: [], changedServices: [], removedServices: [] }}
        isLoading={false} error={null} actions={null} />,
    )
    expect(screen.getByText(/No changes/i)).toBeInTheDocument()
  })
})
