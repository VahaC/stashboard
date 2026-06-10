import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { LxcPowerConfirmDialog } from './LxcPowerConfirmDialog'

/**
 * V6.14 — the Stop / Shutdown confirm dialog must spell out the difference: a
 * graceful Shutdown (safe, async) vs a hard Stop (immediate, possible data loss).
 */
describe('LxcPowerConfirmDialog', () => {
  it('explains a graceful shutdown and confirms with a Shutdown button', () => {
    const onConfirm = vi.fn()
    render(
      <LxcPowerConfirmDialog
        open action="shutdown" vmId={200} name="win11" isVm
        onConfirm={onConfirm} onCancel={() => {}}
      />,
    )
    expect(screen.getByText(/Shut down VM\?/)).toBeInTheDocument()
    expect(screen.getByText(/power off cleanly/i)).toBeInTheDocument()
    // VM wording mentions the guest agent caveat.
    expect(screen.getByText(/no guest agent/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Shutdown' }))
    expect(onConfirm).toHaveBeenCalledOnce()
  })

  it('warns that a hard stop can lose data and confirms with a Stop button', () => {
    const onConfirm = vi.fn()
    render(
      <LxcPowerConfirmDialog
        open action="stop" vmId={101} name="wg" isVm={false}
        onConfirm={onConfirm} onCancel={() => {}}
      />,
    )
    expect(screen.getByText(/Stop container\?/)).toBeInTheDocument()
    expect(screen.getByText(/pulling the power cord/i)).toBeInTheDocument()
    expect(screen.getByText(/unsaved data can be lost/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Stop' }))
    expect(onConfirm).toHaveBeenCalledOnce()
  })

  it('cancels without confirming', () => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    render(
      <LxcPowerConfirmDialog
        open action="shutdown" vmId={200} name="win11" isVm
        onConfirm={onConfirm} onCancel={onCancel}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onCancel).toHaveBeenCalledOnce()
    expect(onConfirm).not.toHaveBeenCalled()
  })
})
