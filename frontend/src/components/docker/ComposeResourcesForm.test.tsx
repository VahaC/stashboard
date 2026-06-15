import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { useState } from 'react'
import type { ComposeResourceConstraints } from '@/lib/types'

// Host capacity comes from the stats stream — feed a fixed 8-CPU / 16-GiB host.
vi.mock('@/lib/docker-stats', () => ({
  streamContainerStats: (
    _c: string, _n: string, _o: unknown,
    onSample: (s: { onlineCpus: number; memoryLimitBytes: number }) => void,
  ) => {
    onSample({ onlineCpus: 8, memoryLimitBytes: 16 * 1024 ** 3 })
    return { abort: () => {}, done: Promise.resolve() }
  },
}))

vi.mock('@/lib/queries', () => ({ useComposeAllocation: vi.fn() }))

import { useComposeAllocation } from '@/lib/queries'
import { parseComposeSize } from '@/lib/compose-size'
import { ComposeResourcesForm } from './ComposeResourcesForm'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockAllocation = useComposeAllocation as unknown as any

const EMPTY: ComposeResourceConstraints = {
  convention: 'deploy',
  cpuLimit: null, cpuReservation: null,
  memLimit: null, memReservation: null,
  pidsLimit: null,
  cpuShares: null, oomKillDisable: null, oomScoreAdj: null, shmSize: null,
  ulimits: [],
}

function Harness({ initial }: { initial: ComposeResourceConstraints }) {
  const [value, setValue] = useState(initial)
  return (
    <ComposeResourcesForm
      connectionId="c1" project="homelab"
      value={value} onChange={setValue}
      capacityContainerName="homelab-web-1"
    />
  )
}

// The form body (capacity panel, sliders, advanced) lives behind a collapsed
// "Resource constraints" disclosure — expand it before asserting on the body.
function expandConstraints() {
  fireEvent.click(screen.getByRole('button', { name: /Resource constraints/i }))
}

beforeEach(() => {
  mockAllocation.mockReset()
  mockAllocation.mockReturnValue({ data: { containerCount: 0, reservedCpus: 0, reservedMemoryBytes: 0 } })
})

describe('parseComposeSize', () => {
  it('parses suffixed and raw byte sizes', () => {
    expect(parseComposeSize('512m')).toBe(512 * 1024 ** 2)
    expect(parseComposeSize('2g')).toBe(2 * 1024 ** 3)
    expect(parseComposeSize('1024')).toBe(1024)
    expect(parseComposeSize(null)).toBeNull()
    expect(parseComposeSize('nope')).toBeNull()
  })
})

describe('ComposeResourcesForm', () => {
  it('bounds the CPU slider by the host capacity', () => {
    render(<Harness initial={EMPTY} />)
    expandConstraints()
    const sliders = screen.getAllByRole('slider')
    // First slider is the CPU limit — max equals the host CPU count.
    expect(sliders[0]).toHaveAttribute('max', '8')
    // Capacity panel reflects the sampled host.
    expect(screen.getByText(/Host capacity:/).parentElement).toHaveTextContent('8 CPUs')
  })

  it('warns about over-commit when draft + others exceeds the host', () => {
    mockAllocation.mockReturnValue({ data: { containerCount: 3, reservedCpus: 4, reservedMemoryBytes: 0 } })
    render(<Harness initial={{ ...EMPTY, cpuLimit: '6' }} />)
    expandConstraints()
    // 4 reserved + 6 draft = 10 > 8 cores.
    expect(screen.getByRole('alert')).toHaveTextContent(/Over-commit/)
  })

  it('disables CPU reservation in the legacy convention', () => {
    render(<Harness initial={{ ...EMPTY, convention: 'legacy' }} />)
    expect(screen.getByText('legacy form')).toBeInTheDocument()
    expandConstraints()
    expect(screen.getByText(/legacy form has no CPU reservation/i)).toBeInTheDocument()
    // The CPU reservation slider is disabled.
    expect(screen.getByLabelText('CPU reservation slider')).toBeDisabled()
  })

  it('edits the CPU limit through the slider', () => {
    render(<Harness initial={EMPTY} />)
    expandConstraints()
    fireEvent.change(screen.getByLabelText('CPU limit slider'), { target: { value: '2' } })
    // The draft line in the capacity panel now reflects 2 CPUs.
    expect(screen.getByText(/this service draft:/)).toHaveTextContent('2 CPUs')
  })
})
