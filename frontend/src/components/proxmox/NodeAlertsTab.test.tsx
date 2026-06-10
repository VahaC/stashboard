import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// Mock the axios instance so the query hooks resolve against canned data.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }))

import { api } from '@/lib/api'
import { NodeAlertsTab, validateThresholds } from './NodeAlertsTab'
import type {
  ProxmoxConnection,
  ProxmoxNodeAlert,
  ProxmoxNodeAlertSettings,
} from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any }

const DEFAULTS = {
  cpuWarn: 80, cpuCrit: 95, memWarn: 85, memCrit: 95,
  storageWarn: 85, storageCrit: 95, tempWarn: 80, tempCrit: 90,
}

function makeSettings(overrides: Partial<ProxmoxNodeAlertSettings> = {}): ProxmoxNodeAlertSettings {
  return {
    enabled: false,
    categories: { cpu: true, memory: true, storage: true, thermal: true, smart: true, network: true },
    thresholds: {
      cpuWarn: null, cpuCrit: null, memWarn: null, memCrit: null,
      storageWarn: null, storageCrit: null, tempWarn: null, tempCrit: null,
    },
    defaults: { ...DEFAULTS },
    lastNotificationSentUtc: null,
    ...overrides,
  }
}

const connection = { id: 'c1', nodeName: 'pve', name: 'home' } as unknown as ProxmoxConnection

function mockGet(settings: ProxmoxNodeAlertSettings, alerts: ProxmoxNodeAlert[]) {
  mockApi.get.mockImplementation((url: string) => {
    if (url.includes('/node/alerts/settings')) return Promise.resolve({ data: settings })
    if (url.includes('/node/alerts')) return Promise.resolve({ data: alerts })
    return Promise.resolve({ data: {} })
  })
}

function renderTab() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <NodeAlertsTab connection={connection} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.put.mockReset()
  mockApi.post.mockReset()
})

describe('NodeAlertsTab — rendering states', () => {
  it('shows the muted hint when alerting is disabled', async () => {
    mockGet(makeSettings({ enabled: false }), [])
    renderTab()
    expect(await screen.findByText(/Alerting is off for this node/i)).toBeInTheDocument()
    // Category + threshold controls only appear once enabled.
    expect(screen.queryByText('Categories')).not.toBeInTheDocument()
  })

  it('renders the all-clear state when enabled with no active alerts (recovered)', async () => {
    mockGet(makeSettings({ enabled: true }), [])
    renderTab()
    expect(await screen.findByText(/No active alerts/i)).toBeInTheDocument()
    expect(screen.getByText('Categories')).toBeInTheDocument()
  })

  it('renders a warning alert with metric, value and threshold', async () => {
    const alert: ProxmoxNodeAlert = {
      category: 'cpu', severity: 'warn', metric: 'CPU', value: 88, threshold: 80,
      firstSeenUtc: null,
    }
    mockGet(makeSettings({ enabled: true }), [alert])
    renderTab()
    expect(await screen.findByText('warning')).toBeInTheDocument()
    expect(screen.getByText(/88% \(threshold 80%\)/)).toBeInTheDocument()
  })

  it('renders a critical alert badge', async () => {
    const alert: ProxmoxNodeAlert = {
      category: 'storage', severity: 'crit', metric: 'local-lvm', value: 97, threshold: 95,
      firstSeenUtc: null,
    }
    mockGet(makeSettings({ enabled: true }), [alert])
    renderTab()
    expect(await screen.findByText('critical')).toBeInTheDocument()
    expect(screen.getByText(/Storage · local-lvm/)).toBeInTheDocument()
  })
})

describe('NodeAlertsTab — enable toggle (optimistic + persisted)', () => {
  it('flips on immediately and PUTs enabled:true', async () => {
    mockGet(makeSettings({ enabled: false }), [])
    // Keep the PUT pending so the checked state we observe is the optimistic one.
    mockApi.put.mockReturnValue(new Promise(() => {}))
    renderTab()

    const checkbox = await screen.findByLabelText(/Node health alerting enabled/i) as HTMLInputElement
    expect(checkbox.checked).toBe(false)

    fireEvent.click(checkbox)

    await waitFor(() => expect(checkbox.checked).toBe(true))
    expect(mockApi.put).toHaveBeenCalledTimes(1)
    const body = mockApi.put.mock.calls[0][1]
    expect(body.enabled).toBe(true)
  })
})

describe('NodeAlertsTab — threshold validation', () => {
  it('blocks save and shows an error when warn ≥ crit', async () => {
    mockGet(makeSettings({ enabled: true }), [])
    renderTab()

    const cpuWarn = await screen.findByLabelText('CPU % warn')
    const cpuCrit = screen.getByLabelText('CPU % crit')
    fireEvent.change(cpuWarn, { target: { value: '96' } })
    fireEvent.change(cpuCrit, { target: { value: '95' } })

    fireEvent.click(screen.getByRole('button', { name: /Save thresholds/i }))

    expect(await screen.findByText(/CPU warn must be below crit/i)).toBeInTheDocument()
    expect(mockApi.put).not.toHaveBeenCalled()
  })

  it('blocks save when a percentage is out of range', async () => {
    mockGet(makeSettings({ enabled: true }), [])
    renderTab()

    const cpuWarn = await screen.findByLabelText('CPU % warn')
    fireEvent.change(cpuWarn, { target: { value: '150' } })
    fireEvent.click(screen.getByRole('button', { name: /Save thresholds/i }))

    expect(await screen.findByText(/CPU warn must be between 1 and 100/i)).toBeInTheDocument()
    expect(mockApi.put).not.toHaveBeenCalled()
  })
})

describe('validateThresholds (pure)', () => {
  const empty = {
    cpuWarn: null, cpuCrit: null, memWarn: null, memCrit: null,
    storageWarn: null, storageCrit: null, tempWarn: null, tempCrit: null,
  }

  it('accepts all-null (use defaults)', () => {
    expect(validateThresholds(empty)).toBeNull()
  })

  it('accepts a valid override pair', () => {
    expect(validateThresholds({ ...empty, cpuWarn: 90, cpuCrit: 98 })).toBeNull()
  })

  it('rejects warn ≥ crit', () => {
    expect(validateThresholds({ ...empty, memWarn: 95, memCrit: 90 })).toMatch(/RAM warn must be below crit/)
  })

  it('rejects out-of-range percentages and temperatures', () => {
    expect(validateThresholds({ ...empty, storageWarn: 0 })).toMatch(/between 1 and 100/)
    expect(validateThresholds({ ...empty, tempWarn: 200 })).toMatch(/between 1 and 150/)
  })
})
