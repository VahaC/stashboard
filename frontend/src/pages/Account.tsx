import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Eye, EyeOff } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { accountApi } from '@/lib/account-api'
import { useAuthStore } from '@/lib/auth-store'
import { useThemeStore, type Theme } from '@/lib/theme-store'
import { parseApiErrors } from '@/lib/utils'
import type { EmailSettings, Profile, TelegramSettings } from '@/lib/types'
import '@/styles/account-page.css'

export function Account() {
  const nav = useNavigate()
  const clear = useAuthStore((s) => s.clear)
  const setLocalTheme = useThemeStore((s) => s.setTheme)
  const [profile, setProfile] = useState<Profile | null>(null)
  const [telegramSettings, setTelegramSettings] = useState<TelegramSettings | null>(null)
  const [emailSettings, setEmailSettings] = useState<EmailSettings | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    accountApi.getProfile().then((p) => {
      setProfile(p)
      if (p.theme === 'system' || p.theme === 'light' || p.theme === 'dark') {
        setLocalTheme(p.theme as Theme)
      }
    }).catch(() => setProfile(null))

    accountApi.getTelegramSettings()
      .then(setTelegramSettings)
      .catch(() => setTelegramSettings(null))

    accountApi.getEmailSettings()
      .then(setEmailSettings)
      .catch(() => setEmailSettings(null))
  }, [reload, setLocalTheme])

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Account</h1>

      <Card>
        <CardHeader>
          <CardTitle>Profile</CardTitle>
          <CardDescription>
            {profile ? (
              <span>
                {profile.email} {profile.emailConfirmed ? '✅ confirmed' : '⚠️ not confirmed'}
                {profile.pendingEmail && <span className="block">Pending change to: {profile.pendingEmail}</span>}
              </span>
            ) : 'Loading…'}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {profile && <DisplayNameForm initial={profile.displayName ?? ''} onSaved={() => setReload((r) => r + 1)} />}
          {profile && !profile.emailConfirmed && (
            <Button
              variant="outline"
              className="account-section-action"
              onClick={() => accountApi.resendConfirmation(profile.email)}
            >
              Resend confirmation email
            </Button>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Appearance</CardTitle>
          <CardDescription>
            Choose how Stashboard looks. <strong>System</strong> follows your operating system.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <ThemeSwitcher />
        </CardContent>
      </Card>

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
            onSaved={() => setReload((r) => r + 1)}
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
            onSaved={() => setReload((r) => r + 1)}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>Change password</CardTitle></CardHeader>
        <CardContent><ChangePasswordForm /></CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Change email</CardTitle>
          <CardDescription>Confirmation link is sent to the new address.</CardDescription>
        </CardHeader>
        <CardContent><ChangeEmailForm onRequested={() => setReload((r) => r + 1)} /></CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-destructive">Delete account</CardTitle>
          <CardDescription>Permanently removes your user, sessions and data. Cannot be undone.</CardDescription>
        </CardHeader>
        <CardContent>
          <DeleteAccountForm onDeleted={() => { clear(); nav('/login') }} />
        </CardContent>
      </Card>
    </div>
  )
}

function DisplayNameForm({ initial, onSaved }: { initial: string; onSaved: () => void }) {
  const [name, setName] = useState(initial)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setFieldErrors({})
    setSaving(true)
    try {
      await accountApi.updateProfile(name || null)
      onSaved()
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? 'Failed to save.')
    } finally {
      setSaving(false)
    }
  }
  return (
    <form onSubmit={submit} className="account-form">
      <Label htmlFor="displayName">Display name</Label>
      <Input id="displayName" value={name} onChange={(e) => setName(e.target.value)} className={fieldErrors['displayname'] ? 'border-destructive' : ''} />
      {fieldErrors['displayname'] && <p className="account-field-error">{fieldErrors['displayname']}</p>}
      {error && <p className="account-form-error">{error}</p>}
      <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
    </form>
  )
}

function TelegramSettingsForm({ initial, onSaved }: { initial: TelegramSettings | null; onSaved: () => void }) {
  const [botToken, setBotToken] = useState(initial?.botToken ?? '')
  const [revealToken, setRevealToken] = useState(false)
  const [chatId, setChatId] = useState(initial?.chatId ?? '')
  const [notificationsEnabled, setNotificationsEnabled] = useState(initial?.notificationsEnabled ?? false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
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
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
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
            onChange={(e) => setBotToken(e.target.value)}
            placeholder="123456:ABC-DEF..."
            className={fieldErrors['bottoken'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
          />
          <button
            type="button"
            className="account-icon-btn"
            onClick={() => setRevealToken((v) => !v)}
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
          onChange={(e) => setChatId(e.target.value)}
          placeholder="123456789"
          className={fieldErrors['chatid'] ? 'border-destructive' : ''}
        />
        {fieldErrors['chatid'] && <p className="account-field-error">{fieldErrors['chatid']}</p>}
      </div>
      <label className="account-checkbox-label">
        <input type="checkbox" checked={notificationsEnabled} onChange={(e) => setNotificationsEnabled(e.target.checked)} />
        Send notifications when a service becomes unavailable
      </label>
      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}
      <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save Telegram settings'}</Button>
    </form>
  )
}

function EmailSettingsForm({ initial, onSaved }: { initial: EmailSettings | null; onSaved: () => void }) {
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

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
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
        // Tri-state: only send the password when the user edited the field.
        password: passwordTouched ? { action: 'Set', value: password } : null,
        fromAddress,
        fromName,
        appBaseUrl,
      })
      setMessage({ kind: 'ok', text: 'Email settings saved.' })
      onSaved()
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save email settings.' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <div className="account-field">
        <Label>Provider</Label>
        <select className="ui-input" value={provider} onChange={(e) => setProvider(e.target.value)}>
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
              onChange={(e) => setHost(e.target.value)}
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
              onChange={(e) => setPort(Number(e.target.value))}
              placeholder="587"
              className={fieldErrors['port'] ? 'border-destructive' : ''}
            />
            {fieldErrors['port'] && <p className="account-field-error">{fieldErrors['port']}</p>}
          </div>
          <label className="account-checkbox-label">
            <input type="checkbox" checked={useStartTls} onChange={(e) => setUseStartTls(e.target.checked)} />
            Use STARTTLS (recommended for port 587)
          </label>
          <div className="account-field">
            <Label>Username</Label>
            <Input
              value={username}
              onChange={(e) => setUsername(e.target.value)}
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
                onChange={(e) => { setPassword(e.target.value); setPasswordTouched(true) }}
                placeholder={initial?.hasPassword ? '•••••••• (stored — leave blank to keep)' : 'App password'}
                className={fieldErrors['password'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
              />
              <button type="button" className="account-icon-btn" onClick={() => setRevealPassword((v) => !v)}>
                {revealPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            </div>
            {fieldErrors['password'] && <p className="account-field-error">{fieldErrors['password']}</p>}
          </div>
          <div className="account-field">
            <Label>From address</Label>
            <Input
              value={fromAddress}
              onChange={(e) => setFromAddress(e.target.value)}
              placeholder="no-reply@stashboard.local"
              className={fieldErrors['fromaddress'] ? 'border-destructive' : ''}
            />
            {fieldErrors['fromaddress'] && <p className="account-field-error">{fieldErrors['fromaddress']}</p>}
          </div>
          <div className="account-field">
            <Label>From name</Label>
            <Input value={fromName} onChange={(e) => setFromName(e.target.value)} placeholder="Stashboard" />
          </div>
        </>
      )}

      <div className="account-field">
        <Label>App base URL</Label>
        <Input
          value={appBaseUrl}
          onChange={(e) => setAppBaseUrl(e.target.value)}
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

function ChangePasswordForm() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setMsg(null)
    setFieldErrors({})
    try {
      await accountApi.changePassword(current, next)
      setMsg({ kind: 'ok', text: 'Password changed. Other sessions have been signed out.' })
      setCurrent(''); setNext('')
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      if (globalError) setMsg({ kind: 'err', text: globalError })
    }
  }
  return (
    <form onSubmit={submit} className="account-form">
      <Label>Current password</Label>
      <Input
        type="password"
        required
        value={current}
        onChange={(e) => setCurrent(e.target.value)}
        className={fieldErrors['currentpassword'] ? 'border-destructive' : ''}
      />
      {fieldErrors['currentpassword'] && <p className="account-field-error">{fieldErrors['currentpassword']}</p>}
      <Label>New password</Label>
      <Input
        type="password"
        required
        minLength={8}
        value={next}
        onChange={(e) => setNext(e.target.value)}
        className={fieldErrors['newpassword'] ? 'border-destructive' : ''}
      />
      {fieldErrors['newpassword'] && <p className="account-field-error">{fieldErrors['newpassword']}</p>}
      {msg && <p className={msg.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{msg.text}</p>}
      <Button type="submit">Change password</Button>
    </form>
  )
}

function ChangeEmailForm({ onRequested }: { onRequested: () => void }) {
  const [email, setEmail] = useState('')
  const [pwd, setPwd] = useState('')
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setMsg(null)
    setFieldErrors({})
    try {
      await accountApi.changeEmail(email, pwd)
      setMsg({ kind: 'ok', text: 'Confirmation link sent to the new address.' })
      setEmail(''); setPwd('')
      onRequested()
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      if (globalError) setMsg({ kind: 'err', text: globalError })
    }
  }
  return (
    <form onSubmit={submit} className="account-form">
      <Label>New email</Label>
      <Input
        type="email"
        required
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        className={fieldErrors['newemail'] || fieldErrors['email'] ? 'border-destructive' : ''}
      />
      {(fieldErrors['newemail'] ?? fieldErrors['email']) && (
        <p className="account-field-error">{fieldErrors['newemail'] ?? fieldErrors['email']}</p>
      )}
      <Label>Current password</Label>
      <Input
        type="password"
        required
        value={pwd}
        onChange={(e) => setPwd(e.target.value)}
        className={fieldErrors['currentpassword'] ? 'border-destructive' : ''}
      />
      {fieldErrors['currentpassword'] && <p className="account-field-error">{fieldErrors['currentpassword']}</p>}
      {msg && <p className={msg.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{msg.text}</p>}
      <Button type="submit">Request email change</Button>
    </form>
  )
}

function DeleteAccountForm({ onDeleted }: { onDeleted: () => void }) {
  const [pwd, setPwd] = useState('')
  const [confirm, setConfirm] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setErr(null)
    setFieldErrors({})
    try { await accountApi.deleteAccount(pwd); onDeleted() }
    catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setErr(globalError)
    }
  }
  return (
    <form onSubmit={submit} className="account-form">
      <Label>Confirm with current password</Label>
      <Input
        type="password"
        required
        value={pwd}
        onChange={(e) => setPwd(e.target.value)}
        className={fieldErrors['currentpassword'] ? 'border-destructive' : ''}
      />
      {fieldErrors['currentpassword'] && <p className="account-field-error">{fieldErrors['currentpassword']}</p>}
      <label className="account-checkbox-label">
        <input type="checkbox" checked={confirm} onChange={(e) => setConfirm(e.target.checked)} />
        I understand this is permanent.
      </label>
      {err && <p className="account-form-error">{err}</p>}
      <Button type="submit" variant="destructive" disabled={!confirm}>Delete account</Button>
    </form>
  )
}
