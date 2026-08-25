import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import QRCode from 'qrcode'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { accountApi, type TwoFactorEnrollment } from '@/lib/account-api'
import { useAuthStore } from '@/lib/auth-store'
import { useThemeStore, type Theme } from '@/lib/theme-store'
import { parseApiErrors } from '@/lib/utils'
import type { Profile } from '@/lib/types'
import '@/styles/account-page.css'

export function Account() {
  const nav = useNavigate()
  const clear = useAuthStore((s) => s.clear)
  const setLocalTheme = useThemeStore((s) => s.setTheme)
  const [profile, setProfile] = useState<Profile | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    accountApi.getProfile().then((p) => {
      setProfile(p)
      if (p.theme === 'system' || p.theme === 'light' || p.theme === 'dark') {
        setLocalTheme(p.theme as Theme)
      }
    }).catch(() => setProfile(null))
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
          <CardTitle>Two-factor authentication</CardTitle>
          <CardDescription>
            Add a second step at sign-in with an authenticator app (TOTP). Stashboard can open a shell on
            your hosts and recreate containers — 2FA protects that behind more than a password.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {profile && (
            <TwoFactorSection
              enabled={profile.twoFactorEnabled}
              onChanged={() => setReload((r) => r + 1)}
              onSignedOut={() => { clear(); nav('/login') }}
            />
          )}
        </CardContent>
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

type TwoFactorMode = 'idle' | 'enrolling' | 'enabledCodes' | 'regeneratedCodes'

function TwoFactorSection({ enabled, onChanged, onSignedOut }: { enabled: boolean; onChanged: () => void; onSignedOut: () => void }) {
  const [mode, setMode] = useState<TwoFactorMode>('idle')
  const [enrollment, setEnrollment] = useState<TwoFactorEnrollment | null>(null)
  const [qr, setQr] = useState<string | null>(null)
  const [code, setCode] = useState('')
  const [codes, setCodes] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!enrollment) return
    let cancelled = false
    QRCode.toDataURL(enrollment.otpauthUri, { margin: 1, width: 200 })
      .then((url) => { if (!cancelled) setQr(url) })
      .catch(() => { if (!cancelled) setQr(null) })
    return () => { cancelled = true }
  }, [enrollment])

  const beginEnroll = async () => {
    setError(null)
    setBusy(true)
    try {
      const e = await accountApi.enrollTwoFactor()
      setEnrollment(e)
      setCode('')
      setMode('enrolling')
    } catch (e: unknown) {
      setError(parseApiErrors(e).globalError ?? 'Failed to start enrollment.')
    } finally {
      setBusy(false)
    }
  }

  const confirmEnable = async (ev: React.FormEvent) => {
    ev.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const result = await accountApi.enableTwoFactor(code)
      setCodes(result.recoveryCodes)
      setEnrollment(null)
      setMode('enabledCodes')
    } catch (e: unknown) {
      setError(parseApiErrors(e).globalError ?? 'Invalid authentication code.')
    } finally {
      setBusy(false)
    }
  }

  const cancelEnroll = () => { setEnrollment(null); setCode(''); setError(null); setMode('idle') }

  // ── One-time recovery-code display (after enable or regenerate) ──
  if (mode === 'enabledCodes') {
    return <RecoveryCodesPanel codes={codes} onDone={() => { setMode('idle'); onChanged() }} />
  }
  if (mode === 'regeneratedCodes') {
    return (
      <RecoveryCodesPanel
        codes={codes}
        signedOut
        onDone={onSignedOut}
      />
    )
  }

  // ── Enrollment in progress ──
  if (mode === 'enrolling' && enrollment) {
    return (
      <form onSubmit={confirmEnable} className="account-form account-form-spaced">
        <p className="twofa-step">1. Scan this QR code with your authenticator app (or enter the key manually).</p>
        <div className="twofa-enroll">
          {qr && <img src={qr} alt="TOTP QR code" className="twofa-qr" width={200} height={200} />}
          <div className="twofa-manual">
            <Label>Manual key</Label>
            <code className="twofa-manual-key">{enrollment.manualKey}</code>
          </div>
        </div>
        <p className="twofa-step">2. Enter the 6-digit code shown in the app to finish.</p>
        <Input
          inputMode="numeric"
          autoComplete="one-time-code"
          placeholder="123456"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          autoFocus
        />
        {error && <p className="account-form-error">{error}</p>}
        <div className="twofa-actions">
          <Button type="submit" disabled={busy || code.trim().length < 6}>{busy ? 'Verifying…' : 'Verify & enable'}</Button>
          <Button type="button" variant="outline" onClick={cancelEnroll} disabled={busy}>Cancel</Button>
        </div>
      </form>
    )
  }

  // ── Enabled: manage recovery codes / disable ──
  if (enabled) {
    return (
      <div className="account-form account-form-spaced">
        <p className="twofa-status">🔒 Two-factor authentication is <strong>enabled</strong>. You'll be asked for a code at each sign-in.</p>
        <TwoFactorPasswordForm
          label="Regenerate recovery codes"
          note="Generates a new set and invalidates the old codes. For your security this signs out all sessions, including this one."
          buttonText="Regenerate codes"
          action={(pwd) => accountApi.regenerateRecoveryCodes(pwd)}
          onSuccess={(result) => { setCodes(result.recoveryCodes); setMode('regeneratedCodes') }}
        />
        <TwoFactorPasswordForm
          label="Disable two-factor authentication"
          note="Removes 2FA from your account and signs out all sessions."
          buttonText="Disable 2FA"
          destructive
          action={(pwd) => accountApi.disableTwoFactor(pwd).then(() => undefined)}
          onSuccess={onSignedOut}
        />
      </div>
    )
  }

  // ── Disabled: idle ──
  return (
    <div className="account-form">
      {error && <p className="account-form-error">{error}</p>}
      <Button onClick={beginEnroll} disabled={busy}>{busy ? 'Starting…' : 'Enable two-factor authentication'}</Button>
    </div>
  )
}

function RecoveryCodesPanel({ codes, onDone, signedOut }: { codes: string[]; onDone: () => void; signedOut?: boolean }) {
  const copy = () => navigator.clipboard?.writeText(codes.join('\n')).catch(() => {})
  return (
    <div className="account-form account-form-spaced">
      <p className="twofa-step"><strong>Save your recovery codes.</strong> Each can be used once if you lose access to your authenticator app. They won't be shown again.</p>
      <ul className="twofa-codes">
        {codes.map((c) => <li key={c}><code>{c}</code></li>)}
      </ul>
      <div className="twofa-actions">
        <Button type="button" variant="outline" onClick={copy}>Copy codes</Button>
        <Button type="button" onClick={onDone}>{signedOut ? 'Sign in again' : "I've saved them"}</Button>
      </div>
      {signedOut && <p className="account-form-success">New codes generated. All sessions have been signed out — sign in again to continue.</p>}
    </div>
  )
}

function TwoFactorPasswordForm<T>({ label, note, buttonText, destructive, action, onSuccess }: {
  label: string
  note: string
  buttonText: string
  destructive?: boolean
  action: (password: string) => Promise<T>
  onSuccess: (result: T) => void
}) {
  const [pwd, setPwd] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const result = await action(pwd)
      onSuccess(result)
    } catch (e: unknown) {
      const { fieldErrors, globalError } = parseApiErrors(e)
      setError(fieldErrors['currentpassword'] ?? globalError ?? 'Failed.')
    } finally {
      setBusy(false)
    }
  }
  return (
    <form onSubmit={submit} className="twofa-subform">
      <Label>{label}</Label>
      <p className="twofa-note">{note}</p>
      <Input type="password" required placeholder="Current password" value={pwd} onChange={(e) => setPwd(e.target.value)} />
      {error && <p className="account-form-error">{error}</p>}
      <Button type="submit" variant={destructive ? 'destructive' : 'outline'} disabled={busy || pwd.length === 0}>
        {busy ? 'Working…' : buttonText}
      </Button>
    </form>
  )
}
