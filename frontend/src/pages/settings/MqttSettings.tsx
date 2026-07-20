import { useEffect, useState } from 'react'
import { Eye, EyeOff, HomeIcon, Radio } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { settingsApi } from '@/lib/settings-api'
import { parseApiErrors } from '@/lib/utils'
import type { MqttSettings as MqttSettingsModel } from '@/lib/types'
import '@/styles/account-page.css'

/**
 * V9.0 — Settings → Home Assistant (MQTT). The app-wide MQTT integration that
 * publishes container / guest / service state to a broker as Home Assistant
 * auto-discovered entities. Mirrors the editable-SMTP model: DB-backed, the broker
 * password encrypted at rest and never returned (presence flag only); changes apply
 * without a restart. Off by default.
 */
export function MqttSettings() {
  const [settings, setSettings] = useState<MqttSettingsModel | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    settingsApi.getMqttSettings()
      .then(setSettings)
      .catch(() => setSettings(null))
  }, [reload])

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Home Assistant (MQTT)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <HomeIcon className="h-5 w-5" /> MQTT / Home Assistant integration
          </CardTitle>
          <CardDescription>
            Publishes the signals Stashboard already collects — each Docker container's and Proxmox
            guest's <strong>running</strong> state, each Docker container's <strong>update-available</strong>,
            and each monitored service's <strong>online/offline</strong> health — plus the signals it
            <strong> computes</strong>: <strong>pending-update counts</strong> (per Docker host, Proxmox node
            and LXC), the per-node <strong>alert verdict</strong> (a <code>problem</code> sensor with the
            CPU / memory / storage / thermal / SMART / network breakdown), each guest's <strong>backup
            freshness</strong>, and a <strong>Stashboard</strong> roll-up device (containers / guests /
            services / hosts / updates at a glance) — all to your existing MQTT broker (e.g. Mosquitto) as
            Home Assistant <strong>auto-discovered</strong> entities. No HA YAML, no HACS add-on: turn it on,
            point it at your broker, and the entities appear in Home Assistant within a check cycle. This is
            <strong> publish-only</strong> — Home Assistant observes, it can't control anything. Off by default.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <MqttSettingsForm
            key={`${settings?.enabled ?? false}|${settings?.host ?? ''}|${settings?.username ?? ''}|${settings?.hasPassword ?? false}|${settings?.entityPrefix ?? ''}|${settings?.deviceName ?? ''}|${settings?.manufacturer ?? ''}`}
            initial={settings}
            onSaved={() => setReload((value) => value + 1)}
          />

          <section className="host-shell-settings-section">
            <h3>How it shows up in Home Assistant</h3>
            <p>
              Every entity is prefixed with your <strong>entity prefix</strong> (default <code>stashboard</code>),
              so they're trivial to spot and filter — e.g. <code>binary_sensor.stashboard_jellyfin_running</code>.
              Each real object — a Docker container, a Proxmox guest, a service — is its <em>own</em> Home
              Assistant device under a single <strong>Stashboard</strong> hub (a container and an LXC that merely
              share a name stay separate devices). A service's health sensor joins the device of the container or
              guest you <em>linked</em> it to, so its running + update + health sit together. All
              entities reference a shared availability topic registered as the broker <strong>Last Will</strong>,
              so they flip to <em>unavailable</em> the moment Stashboard stops or the connection drops. When a
              container / guest / service disappears, its retained topics are cleared so Home Assistant removes the
              entity rather than leaving an orphan.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}

export function MqttSettingsForm({ initial, onSaved }: { initial: MqttSettingsModel | null; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(initial?.enabled ?? false)
  const [host, setHost] = useState(initial?.host ?? '')
  const [port, setPort] = useState(initial?.port ?? 1883)
  const [useTls, setUseTls] = useState(initial?.useTls ?? false)
  const [allowUntrustedTls, setAllowUntrustedTls] = useState(initial?.allowUntrustedTls ?? false)
  const [username, setUsername] = useState(initial?.username ?? '')
  const [password, setPassword] = useState('')
  const [passwordTouched, setPasswordTouched] = useState(false)
  const [revealPassword, setRevealPassword] = useState(false)
  const [clientId, setClientId] = useState(initial?.clientId ?? 'stashboard')
  const [discoveryPrefix, setDiscoveryPrefix] = useState(initial?.discoveryPrefix ?? 'homeassistant')
  const [entityPrefix, setEntityPrefix] = useState(initial?.entityPrefix ?? 'stashboard')
  const [deviceName, setDeviceName] = useState(initial?.deviceName ?? 'Stashboard')
  const [manufacturer, setManufacturer] = useState(initial?.manufacturer ?? 'Stashboard')
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    setFieldErrors({})
    try {
      await settingsApi.updateMqttSettings({
        enabled,
        host,
        port,
        useTls,
        allowUntrustedTls,
        username,
        password: passwordTouched ? { action: 'Set', value: password } : null,
        clientId,
        discoveryPrefix,
        entityPrefix,
        deviceName,
        manufacturer,
      })
      setMessage({ kind: 'ok', text: 'MQTT settings saved.' })
      onSaved()
    } catch (error: unknown) {
      const { fieldErrors: parsedFieldErrors, globalError } = parseApiErrors(error)
      setFieldErrors(parsedFieldErrors)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save MQTT settings.' })
    } finally {
      setSaving(false)
    }
  }

  const test = async () => {
    setTesting(true)
    setMessage(null)
    try {
      const result = await settingsApi.testMqttSettings()
      setMessage(result.reachable
        ? { kind: 'ok', text: 'Broker reachable.' }
        : { kind: 'err', text: result.error ?? 'Broker unreachable.' })
    } catch {
      setMessage({ kind: 'err', text: 'Test failed. Save your settings first, then try again.' })
    } finally {
      setTesting(false)
    }
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <label className="account-checkbox-label">
        <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
        Enable the MQTT / Home Assistant integration
      </label>

      <div className="account-field">
        <Label>Broker host</Label>
        <Input
          value={host}
          onChange={(event) => setHost(event.target.value)}
          placeholder="192.168.1.10 or mosquitto.lan"
          className={fieldErrors['host'] ? 'border-destructive' : ''}
        />
        {fieldErrors['host'] && <p className="account-field-error">{fieldErrors['host']}</p>}
      </div>

      <div className="account-field">
        <Label>Port</Label>
        <Input
          type="number"
          value={port}
          onChange={(event) => setPort(Number(event.target.value))}
          placeholder="1883"
          className={fieldErrors['port'] ? 'border-destructive' : ''}
        />
        {fieldErrors['port'] && <p className="account-field-error">{fieldErrors['port']}</p>}
      </div>

      <label className="account-checkbox-label">
        <input type="checkbox" checked={useTls} onChange={(event) => setUseTls(event.target.checked)} />
        Connect over TLS (typically port 8883)
      </label>

      {useTls && (
        <label className="account-checkbox-label">
          <input
            type="checkbox"
            checked={allowUntrustedTls}
            onChange={(event) => setAllowUntrustedTls(event.target.checked)}
          />
          Accept a self-signed / untrusted broker certificate
        </label>
      )}

      <div className="account-field">
        <Label>Username</Label>
        <Input
          value={username}
          onChange={(event) => setUsername(event.target.value)}
          placeholder="(optional)"
          className={fieldErrors['username'] ? 'border-destructive' : ''}
        />
        {fieldErrors['username'] && <p className="account-field-error">{fieldErrors['username']}</p>}
      </div>

      <div className="account-field">
        <Label>Password</Label>
        <div className="account-inline-row">
          <Input
            type={revealPassword ? 'text' : 'password'}
            value={password}
            onChange={(event) => { setPassword(event.target.value); setPasswordTouched(true) }}
            placeholder={initial?.hasPassword ? '•••••••• (stored — leave blank to keep)' : '(optional)'}
            className={fieldErrors['password'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
          />
          <button type="button" className="account-icon-btn" onClick={() => setRevealPassword((value) => !value)}>
            {revealPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        </div>
        {fieldErrors['password'] && <p className="account-field-error">{fieldErrors['password']}</p>}
      </div>

      <div className="account-field">
        <Label>Client ID</Label>
        <Input value={clientId} onChange={(event) => setClientId(event.target.value)} placeholder="stashboard" />
      </div>

      <div className="account-field">
        <Label>Discovery prefix</Label>
        <Input
          value={discoveryPrefix}
          onChange={(event) => setDiscoveryPrefix(event.target.value)}
          placeholder="homeassistant"
        />
        <p className="host-shell-settings-note">
          Must match Home Assistant's MQTT discovery prefix (default <code>homeassistant</code>).
        </p>
      </div>

      <div className="account-field">
        <Label>Entity prefix</Label>
        <Input
          value={entityPrefix}
          onChange={(event) => setEntityPrefix(event.target.value)}
          placeholder="stashboard"
        />
        <p className="host-shell-settings-note">
          Prefixes every published entity (e.g. <code>binary_sensor.stashboard_jellyfin_running</code>). Changing
          it re-publishes everything under the new ids and clears the old ones.
        </p>
      </div>

      <div className="account-field">
        <Label>Hub device name</Label>
        <Input value={deviceName} onChange={(event) => setDeviceName(event.target.value)} placeholder="Stashboard" />
        <p className="host-shell-settings-note">
          Name of the single Home Assistant <strong>hub device</strong> every entity nests under (the group label in
          HA's device list). Default <code>Stashboard</code>.
        </p>
      </div>

      <div className="account-field">
        <Label>Manufacturer</Label>
        <Input value={manufacturer} onChange={(event) => setManufacturer(event.target.value)} placeholder="Stashboard" />
        <p className="host-shell-settings-note">
          The <strong>manufacturer</strong> shown on every published device in Home Assistant. Default
          <code> Stashboard</code>.
        </p>
      </div>

      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}

      <div className="account-inline-row">
        <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save MQTT settings'}</Button>
        <Button type="button" variant="outline" onClick={test} disabled={testing}>
          <Radio className="h-4 w-4" /> {testing ? 'Testing…' : 'Test connection'}
        </Button>
      </div>
    </form>
  )
}
