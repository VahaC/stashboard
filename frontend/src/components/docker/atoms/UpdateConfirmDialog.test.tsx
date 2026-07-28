import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'

import { UpdateConfirmDialog } from './UpdateConfirmDialog'
import type { DockerWatchUpdateResponse } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */

function buildResponse(status: string, error: string | null = null): DockerWatchUpdateResponse {
  return {
    attempt: {
      id: 'a1',
      dockerWatchId: 'w1',
      webResourceId: null,
      dockerConnectionId: 'c1',
      containerId: null,
      actionType: 'Update',
      status,
      imageReference: 'vahac/stashboard:latest',
      containerName: 'stashboard-app',
      previousDigest: null,
      newDigest: null,
      error,
      completedUtc: '2026-06-27T00:00:00Z',
      createdUtc: '2026-06-27T00:00:00Z',
      healthVerified: false,
      healthVerifiedUtc: null,
    },
    watch: {} as any,
  } as DockerWatchUpdateResponse
}

function renderDialog(onConfirm: () => Promise<DockerWatchUpdateResponse>) {
  return render(
    <UpdateConfirmDialog
      open
      imageReference="vahac/stashboard:latest"
      containerName="stashboard-app"
      updateAvailable
      onConfirm={onConfirm}
      onClose={() => {}}
    />,
  )
}

describe('UpdateConfirmDialog — self-update', () => {
  it('renders a distinct "Scheduled" state when the backend offloads to a helper', async () => {
    const onConfirm = vi.fn().mockResolvedValue(
      buildResponse('Scheduled', 'Stashboard is updating itself in a detached helper container.'),
    )
    renderDialog(onConfirm)

    fireEvent.click(screen.getByRole('button', { name: /update now/i }))

    await waitFor(() => expect(screen.getByText(/self-update scheduled/i)).toBeInTheDocument())
    // The detached-helper message is surfaced, and we must NOT claim it's done.
    expect(screen.getByText(/detached helper container/i)).toBeInTheDocument()
    expect(screen.queryByText(/container .* updated\./i)).not.toBeInTheDocument()
  })

  it('still renders success for a normal recreate', async () => {
    const onConfirm = vi.fn().mockResolvedValue(buildResponse('Success'))
    renderDialog(onConfirm)

    fireEvent.click(screen.getByRole('button', { name: /update now/i }))

    await waitFor(() => expect(screen.getByText(/update complete/i)).toBeInTheDocument())
  })

  it('renders failure for a recreate error', async () => {
    const onConfirm = vi.fn().mockResolvedValue(buildResponse('RecreateFailed', 'stop failed'))
    renderDialog(onConfirm)

    fireEvent.click(screen.getByRole('button', { name: /update now/i }))

    await waitFor(() => expect(screen.getByText(/update finished with errors/i)).toBeInTheDocument())
  })
})
