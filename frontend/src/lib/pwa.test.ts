import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { registerServiceWorker } from './register-sw'

const read = (rel: string) => readFileSync(fileURLToPath(new URL(rel, import.meta.url)), 'utf8')

describe('web app manifest', () => {
  const manifest = JSON.parse(read('../../public/manifest.webmanifest'))

  it('declares the fields a browser needs to treat the app as installable', () => {
    expect(manifest.name).toBeTruthy()
    expect(manifest.short_name).toBeTruthy()
    expect(manifest.start_url).toBe('/')
    expect(manifest.display).toBe('standalone')
  })

  it('ships 192px + 512px icons plus a maskable variant', () => {
    const sizes = manifest.icons.map((i: { sizes: string }) => i.sizes)
    expect(sizes).toContain('192x192')
    expect(sizes).toContain('512x512')
    const purposes = manifest.icons.map((i: { purpose: string }) => i.purpose)
    expect(purposes).toContain('maskable')
  })
})

describe('service worker script', () => {
  const sw = read('../../public/sw.js')

  it('handles fetch, push and notificationclick', () => {
    expect(sw).toMatch(/addEventListener\(['"]fetch['"]/)
    expect(sw).toMatch(/addEventListener\(['"]push['"]/)
    expect(sw).toMatch(/addEventListener\(['"]notificationclick['"]/)
  })

  it('never intercepts API or upload requests', () => {
    expect(sw).toMatch(/\/api/)
    expect(sw).toMatch(/\/uploads/)
  })
})

describe('registerServiceWorker', () => {
  const setSecureContext = (value: boolean) =>
    Object.defineProperty(window, 'isSecureContext', { value, configurable: true })

  afterEach(() => {
    vi.unstubAllEnvs()
    vi.restoreAllMocks()
  })

  // Capture the 'load' handler directly instead of dispatching a global event,
  // so a listener from one test can't leak into the next.
  const captureLoadHandler = () => {
    let handler: (() => void) | null = null
    vi.spyOn(window, 'addEventListener').mockImplementation((type, cb) => {
      if (type === 'load') handler = cb as () => void
    })
    return () => handler
  }

  it('registers /sw.js on load in a secure production context', () => {
    vi.stubEnv('PROD', true)
    setSecureContext(true)
    const register = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'serviceWorker', { value: { register }, configurable: true })
    const getHandler = captureLoadHandler()

    registerServiceWorker()
    getHandler()?.()

    expect(register).toHaveBeenCalledWith('/sw.js')
  })

  it('does not register in an insecure context (plain http on the LAN)', () => {
    vi.stubEnv('PROD', true)
    setSecureContext(false)
    const register = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'serviceWorker', { value: { register }, configurable: true })
    const getHandler = captureLoadHandler()

    registerServiceWorker()

    expect(getHandler()).toBeNull()
    expect(register).not.toHaveBeenCalled()
  })
})
