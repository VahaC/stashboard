import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { accountApi } from '@/lib/account-api'
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
