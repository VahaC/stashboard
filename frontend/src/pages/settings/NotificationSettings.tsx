import { useEffect, useState } from 'react'
import { Eye, EyeOff } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { accountApi } from '@/lib/account-api'
import { settingsApi } from '@/lib/settings-api'
import { parseApiErrors } from '@/lib/utils'
import type { AppriseSettings, AppriseTargetResult, EmailSettings, TelegramSettings } from '@/lib/types'
import '@/styles/account-page.css'

export function NotificationSettings() {
  const [telegramSettings, setTelegramSettings] = useState<TelegramSettings | null>(null)
  const [emailSettings, setEmailSettings] = useState<EmailSettings | null>(null)
  const [appriseSettings, setAppriseSettings] = useState<AppriseSettings | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    accountApi.getTelegramSettings()
      .then(setTelegramSettings)
      .catch(() => setTelegramSettings(null))

    accountApi.getEmailSettings()
      .then(setEmailSettings)
      .catch(() => setEmailSettings(null))

    settingsApi.getAppriseSettings()
      .then(setAppriseSettings)
      .catch(() => setAppriseSettings(null))
  }, [reload])

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Notifications</h1>

      <Card>
        <CardHeader>
          <CardTitle>Telegram notifications</CardTitle>
          <CardDescription>
            Connect a Telegram bot and receive alerts when a service becomes unavailable.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <TelegramSettingsForm
            key={`${telegramSettings?.botToken ?? ''}|${telegramSettings?.chatId ?? ''}|${telegramSettings?.notificationsEnabled ?? false}`}
            initial={telegramSettings}
            onSaved={() => setReload((value) => value + 1)}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Email server (SMTP)</CardTitle>
          <CardDescription>
            Configure the mail server used for confirmation, password-reset and notification emails.
            Leave the provider on <strong>Log only</strong> to print emails to the server log instead of sending.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <EmailSettingsForm
            key={`${emailSettings?.provider ?? ''}|${emailSettings?.host ?? ''}|${emailSettings?.username ?? ''}|${emailSettings?.hasPassword ?? false}`}
            initial={emailSettings}
            onSaved={() => setReload((value) => value + 1)}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Apprise notifications</CardTitle>
          <CardDescription>
            Fan notifications out to Discord, ntfy, Gotify, Slack, and 90+ other services through your own{' '}
            <a href="https://github.com/caronc/apprise-api" target="_blank" rel="noreferrer" className="underline">Apprise API</a>.
            Set the base URL of your Apprise instance (or the stateless <code>/notify</code> endpoint) and one
            Apprise URL per line. Enable the per-watch / per-host / per-service Apprise toggle to route those alerts here.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <AppriseSettingsForm
            key={`${appriseSettings?.enabled ?? false}|${appriseSettings?.baseUrl ?? ''}|${appriseSettings?.hasUrls ?? false}|${appriseSettings?.urlCount ?? 0}`}
            initial={appriseSettings}
            onSaved={() => setReload((value) => value + 1)}
          />
        </CardContent>
      </Card>
    </div>
  )
}

export function AppriseSettingsForm({ initial, onSaved }: { initial: AppriseSettings | null; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(initial?.enabled ?? false)
  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? '')
  const [urls, setUrls] = useState('')
  const [urlsTouched, setUrlsTouched] = useState(false)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [testResults, setTestResults] = useState<AppriseTargetResult[] | null>(null)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    setTestResults(null)
    setFieldErrors({})
    try {
      await settingsApi.updateAppriseSettings({
        enabled,
        baseUrl,
        urls: urlsTouched ? { action: 'Set', value: urls } : null,
      })
      setMessage({ kind: 'ok', text: 'Apprise settings saved.' })
      onSaved()
    } catch (error: unknown) {
      const { fieldErrors: parsedFieldErrors, globalError } = parseApiErrors(error)
      setFieldErrors(parsedFieldErrors)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save Apprise settings.' })
    } finally {
      setSaving(false)
    }
  }

  const sendTest = async () => {
    setTesting(true)
    setMessage(null)
    setTestResults(null)
    try {
      const result = await settingsApi.testAppriseSettings()
      setTestResults(result.results)
      if (result.error) setMessage({ kind: 'err', text: result.error })
      else setMessage({ kind: result.ok ? 'ok' : 'err', text: result.ok ? 'Test notification sent to all targets.' : 'One or more targets failed — see below.' })
    } catch {
      setMessage({ kind: 'err', text: 'Failed to send test notification.' })
    } finally {
      setTesting(false)
    }
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <label className="account-checkbox-label">
        <input type="checkbox" checked={enabled} onChange={(event) => setEnabled(event.target.checked)} />
        Enable Apprise notifications
      </label>
      <div className="account-field">
        <Label>Apprise base URL</Label>
        <Input
          value={baseUrl}
          onChange={(event) => setBaseUrl(event.target.value)}
          placeholder="http://apprise:8000"
          className={fieldErrors['baseurl'] ? 'border-destructive' : ''}
        />
        {fieldErrors['baseurl'] && <p className="account-field-error">{fieldErrors['baseurl']}</p>}
      </div>
      <div className="account-field">
        <Label>Apprise URLs (one per line)</Label>
        <textarea
          className="ui-input"
          rows={4}
          style={{ height: 'auto', minHeight: '7rem', resize: 'vertical', lineHeight: 1.6, fontFamily: 'var(--font-mono, monospace)' }}
          value={urls}
          onChange={(event) => { setUrls(event.target.value); setUrlsTouched(true) }}
          placeholder={initial?.hasUrls
            ? `•••••••• (${initial.urlCount} stored — leave blank to keep)`
            : 'discord://webhook_id/webhook_token\nntfy://ntfy.sh/my-topic\ngotify://gotify.example.com/token'}
        />
        {initial && initial.targets.length > 0 && (
          <p className="text-sm text-muted-foreground">Configured targets: {initial.targets.join(', ')}</p>
        )}
      </div>
      {testResults && (
        <ul className="account-form-spaced" style={{ margin: 0, paddingLeft: '1.25rem' }}>
          {testResults.map((r, i) => (
            <li key={i} className={r.success ? 'account-form-success' : 'account-form-error'}>
              {r.success ? '✅' : '❌'} {r.target}{r.error ? ` — ${r.error}` : ''}
            </li>
          ))}
        </ul>
      )}
      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}
      <div style={{ display: 'flex', gap: '0.75rem', flexWrap: 'wrap' }}>
        <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save Apprise settings'}</Button>
        <Button type="button" variant="outline" disabled={testing || !initial?.hasUrls} onClick={sendTest}>
          {testing ? 'Sending…' : 'Send test'}
        </Button>
      </div>
    </form>
  )
}

export function TelegramSettingsForm({ initial, onSaved }: { initial: TelegramSettings | null; onSaved: () => void }) {
  const [botToken, setBotToken] = useState(initial?.botToken ?? '')
  const [revealToken, setRevealToken] = useState(false)
  const [chatId, setChatId] = useState(initial?.chatId ?? '')
  const [notificationsEnabled, setNotificationsEnabled] = useState(initial?.notificationsEnabled ?? false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    setFieldErrors({})
    try {
      await accountApi.updateTelegramSettings({
        botToken: botToken || null,
        chatId: chatId || null,
        notificationsEnabled,
      })
      setMessage({ kind: 'ok', text: 'Telegram settings saved.' })
      onSaved()
    } catch (error: unknown) {
      const { fieldErrors: parsedFieldErrors, globalError } = parseApiErrors(error)
      setFieldErrors(parsedFieldErrors)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save Telegram settings.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <div className="account-field">
        <Label>Bot token</Label>
        <div className="account-inline-row">
          <Input
            type={revealToken ? 'text' : 'password'}
            value={botToken}
            onChange={(event) => setBotToken(event.target.value)}
            placeholder="123456:ABC-DEF..."
            className={fieldErrors['bottoken'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
          />
          <button
            type="button"
            className="account-icon-btn"
            onClick={() => setRevealToken((value) => !value)}
          >
            {revealToken ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        </div>
        {fieldErrors['bottoken'] && <p className="account-field-error">{fieldErrors['bottoken']}</p>}
      </div>
      <div className="account-field">
        <Label>Chat ID</Label>
        <Input
          value={chatId}
          onChange={(event) => setChatId(event.target.value)}
          placeholder="123456789"
          className={fieldErrors['chatid'] ? 'border-destructive' : ''}
        />
        {fieldErrors['chatid'] && <p className="account-field-error">{fieldErrors['chatid']}</p>}
      </div>
      <label className="account-checkbox-label">
        <input type="checkbox" checked={notificationsEnabled} onChange={(event) => setNotificationsEnabled(event.target.checked)} />
        Send notifications when a service becomes unavailable
      </label>
      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}
      <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save Telegram settings'}</Button>
    </form>
  )
}

export function EmailSettingsForm({ initial, onSaved }: { initial: EmailSettings | null; onSaved: () => void }) {
  const [provider, setProvider] = useState(initial?.provider ?? 'LogOnly')
  const [host, setHost] = useState(initial?.host ?? '')
  const [port, setPort] = useState(initial?.port ?? 587)
  const [useStartTls, setUseStartTls] = useState(initial?.useStartTls ?? true)
  const [username, setUsername] = useState(initial?.username ?? '')
  const [password, setPassword] = useState('')
  const [passwordTouched, setPasswordTouched] = useState(false)
  const [revealPassword, setRevealPassword] = useState(false)
  const [fromAddress, setFromAddress] = useState(initial?.fromAddress ?? '')
  const [fromName, setFromName] = useState(initial?.fromName ?? '')
  const [appBaseUrl, setAppBaseUrl] = useState(initial?.appBaseUrl ?? '')
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const isSmtp = provider === 'Smtp'

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    setFieldErrors({})
    try {
      await accountApi.updateEmailSettings({
        provider,
        host,
        port,
        useStartTls,
        username,
        password: passwordTouched ? { action: 'Set', value: password } : null,
        fromAddress,
        fromName,
        appBaseUrl,
      })
      setMessage({ kind: 'ok', text: 'Email settings saved.' })
      onSaved()
    } catch (error: unknown) {
      const { fieldErrors: parsedFieldErrors, globalError } = parseApiErrors(error)
      setFieldErrors(parsedFieldErrors)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save email settings.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <div className="account-field">
        <Label>Provider</Label>
        <select className="ui-input" value={provider} onChange={(event) => setProvider(event.target.value)}>
          <option value="LogOnly">Log only (don't send)</option>
          <option value="Smtp">SMTP</option>
        </select>
      </div>

      {isSmtp && (
        <>
          <div className="account-field">
            <Label>SMTP host</Label>
            <Input
              value={host}
              onChange={(event) => setHost(event.target.value)}
              placeholder="smtp.gmail.com"
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
              placeholder="587"
              className={fieldErrors['port'] ? 'border-destructive' : ''}
            />
            {fieldErrors['port'] && <p className="account-field-error">{fieldErrors['port']}</p>}
          </div>
          <label className="account-checkbox-label">
            <input type="checkbox" checked={useStartTls} onChange={(event) => setUseStartTls(event.target.checked)} />
            Use STARTTLS (recommended for port 587)
          </label>
          <div className="account-field">
            <Label>Username</Label>
            <Input
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              placeholder="you@gmail.com"
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
                placeholder={initial?.hasPassword ? '•••••••• (stored — leave blank to keep)' : 'App password'}
                className={fieldErrors['password'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
              />
              <button type="button" className="account-icon-btn" onClick={() => setRevealPassword((value) => !value)}>
                {revealPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
            {fieldErrors['password'] && <p className="account-field-error">{fieldErrors['password']}</p>}
          </div>
          <div className="account-field">
            <Label>From address</Label>
            <Input
              value={fromAddress}
              onChange={(event) => setFromAddress(event.target.value)}
              placeholder="no-reply@stashboard.local"
              className={fieldErrors['fromaddress'] ? 'border-destructive' : ''}
            />
            {fieldErrors['fromaddress'] && <p className="account-field-error">{fieldErrors['fromaddress']}</p>}
          </div>
          <div className="account-field">
            <Label>From name</Label>
            <Input value={fromName} onChange={(event) => setFromName(event.target.value)} placeholder="Stashboard" />
          </div>
        </>
      )}

      <div className="account-field">
        <Label>App base URL</Label>
        <Input
          value={appBaseUrl}
          onChange={(event) => setAppBaseUrl(event.target.value)}
          placeholder="https://stashboard.example.com"
          className={fieldErrors['appbaseurl'] ? 'border-destructive' : ''}
        />
        {fieldErrors['appbaseurl'] && <p className="account-field-error">{fieldErrors['appbaseurl']}</p>}
      </div>

      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}
      <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save email settings'}</Button>
    </form>
  )
}
