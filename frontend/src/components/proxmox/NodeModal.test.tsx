import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// Mock the axios instance so the query hooks resolve against canned data.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }))

import { api } from '@/lib/api'
import { NodeModal, type NodeModalTab } from './NodeModal'
import type { ProxmoxConnection } from '@/lib/types'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; put: any; post: any }

const connection = {
  id: 'c1', nodeName: 'pve', name: 'home', apiBaseUrl: 'https://pve.lan:8006',
  telemetryPollSeconds: 20,
} as unknown as ProxmoxConnection

// Canned payloads per endpoint; tests override individual keys.
function defaultRoutes(): Record<string, unknown> {
  return {
    'node/status': {
      cpuModel: 'Test', sockets: 1, cpus: 2, cores: 2, cpuMhz: 3000, hvm: true,
      cpuFraction: 0.1, ioWaitFraction: 0.01, load1: 0.2, load5: 0.2, load15: 0.2,
      memTotal: 1000, memUsed: 500, memFree: 500, swapTotal: 0, swapUsed: 0,
      rootTotal: 1000, rootUsed: 100, uptimeSeconds: 100, kernelVersion: 'k',
      pveVersion: 'pve', subscriptionStatus: null, subscriptionLevel: null,
    },
    'node/cpu': {
      available: true, error: null, stealPercent: 6, memAvailableBytes: 700,
      cores: [
        { core: 0, utilPercent: 50, stealPercent: 0 },
        { core: 1, utilPercent: 50, stealPercent: 12 },
      ],
    },
    'node/rrddata': [],
    'node/storage': [],
    'node/diskio': {
      available: true, error: null,
      disks: [{ device: 'sda', readBytesPerSec: 1024000, writeBytesPerSec: 0, readIops: 100, writeIops: 0, readAwaitMs: 1, writeAwaitMs: null }],
    },
    'node/thinpools': {
      available: true, error: null,
      pools: [{ name: 'data', volumeGroup: 'pve', sizeBytes: 1000000000, dataPercent: 82.5, metadataPercent: 10.2 }],
    },
    'node/disks': [
      { devPath: '/dev/sda', model: 'X', serial: null, vendor: null, type: 'ssd', size: 1000000000, health: 'PASSED', wearoutPercent: null, rpm: null, used: null },
    ],
    'node/disks/smart': { health: 'PASSED', type: 'ssd', attributes: [], text: null },
    'node/disks/selftest': {
      available: true, error: null, lastTestType: 'Extended offline', lastTestStatus: 'Completed without error',
      lastTestPowerOnHours: 12450, powerOnHours: 12450, reallocatedSectors: 0, pendingSectors: 0, uncorrectableSectors: 0,
    },
    'node/interfaces': {
      available: true, error: null,
      interfaces: [{ iface: 'eth0', rxBytesPerSec: 2000, txBytesPerSec: 3000, rxErrors: 5, txErrors: 3, rxDropped: 0, txDropped: 0, speedMbps: 1000, duplex: 'full', operState: 'up' }],
    },
    'node/network': [
      { iface: 'eth0', type: 'eth', active: true, autostart: true, method: 'static', address: null, cidr: '192.168.1.2/24', gateway: null, bridgePorts: null, bondSlaves: null },
    ],
    'node/sensors': {
      available: true, error: null,
      readings: [
        { chip: 'coretemp', label: 'Package id 0', tempC: 45, highC: 80, critC: 100, rpm: null, volts: null, watts: null },
        { chip: 'nct', label: 'fan1', tempC: null, highC: null, critC: null, rpm: 1200, volts: null, watts: null },
        { chip: 'nct', label: 'Vcore', tempC: null, highC: null, critC: null, rpm: null, volts: 0.95, watts: null },
        { chip: 'nct', label: 'power1', tempC: null, highC: null, critC: null, rpm: null, volts: null, watts: 42.5 },
      ],
    },
  }
}

function mockRoutes(overrides: Record<string, unknown> = {}) {
  const routes = { ...defaultRoutes(), ...overrides }
  mockApi.get.mockImplementation((url: string) => {
    // Most specific first: selftest / smart before the bare disks list.
    if (url.includes('/node/disks/selftest')) return Promise.resolve({ data: routes['node/disks/selftest'] })
    if (url.includes('/node/disks/smart')) return Promise.resolve({ data: routes['node/disks/smart'] })
    const key = Object.keys(routes).find((k) => url.includes(`/${k}`) && !url.includes('/node/disks/'))
      ?? Object.keys(routes).find((k) => url.includes(`/${k}`))
    return Promise.resolve({ data: key ? routes[key] : {} })
  })
}

function renderModal(initialTab: NodeModalTab) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={qc}>
      <NodeModal connection={connection} initialTab={initialTab} onClose={() => {}} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.put.mockReset()
  mockApi.post.mockReset()
})

describe('NodeModal — V6.8.2 deep telemetry', () => {
  it('renders per-core utilisation bars + steal on the CPU/RAM tab', async () => {
    mockRoutes()
    renderModal('cpuram')
    expect(await screen.findByText('Per-core utilisation')).toBeInTheDocument()
    expect(await screen.findByText('#0')).toBeInTheDocument()
    expect(await screen.findByText('#1')).toBeInTheDocument()
    expect(screen.getByText('steal 6.0%')).toBeInTheDocument()
  })

  it('degrades the per-core section to a note when SSH is unavailable', async () => {
    mockRoutes({ 'node/cpu': { available: false, error: 'SSH is not configured.', cores: [], stealPercent: null, memAvailableBytes: null } })
    renderModal('cpuram')
    expect(await screen.findByText(/Add SSH credentials to this host to read/)).toBeInTheDocument()
  })

  it('renders the disk-IO section and thin-pool warnings on the Storage tab', async () => {
    mockRoutes()
    renderModal('storage')
    expect(await screen.findByText('Disk IO')).toBeInTheDocument()
    expect(await screen.findByText('sda')).toBeInTheDocument()
    expect(await screen.findByText('Thin pools')).toBeInTheDocument()
    expect(screen.getByText(/data 82\.5%/)).toBeInTheDocument()
  })

  it('shows the self-test badge when a disk row is expanded', async () => {
    mockRoutes()
    renderModal('storage')
    // Radix portals the dialog content to document.body, so query the document.
    await screen.findByText('Disks & SMART')
    const details = await waitFor(() => {
      const el = document.querySelector('details')
      if (!el) throw new Error('disk row not yet rendered')
      return el as HTMLDetailsElement
    })
    details.open = true
    fireEvent(details, new Event('toggle'))
    expect(await screen.findByText('Last self-test')).toBeInTheDocument()
    expect(await screen.findByText(/Extended offline: Completed without error/)).toBeInTheDocument()
  })

  it('renders per-interface throughput + an error badge on the Network tab', async () => {
    mockRoutes()
    renderModal('network')
    expect(await screen.findByText('eth0')).toBeInTheDocument()
    expect(await screen.findByText('8 err')).toBeInTheDocument()   // rx 5 + tx 3
  })

  it('renders voltage + power sections on the Sensors tab', async () => {
    mockRoutes()
    renderModal('sensors')
    expect(await screen.findByText('Voltages')).toBeInTheDocument()
    expect(await screen.findByText('Vcore')).toBeInTheDocument()
    expect(screen.getByText('0.95 V')).toBeInTheDocument()
    expect(await screen.findByText('Power')).toBeInTheDocument()
    expect(screen.getByText('42.5 W')).toBeInTheDocument()
  })
})
