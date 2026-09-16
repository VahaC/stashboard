import { useEffect, useState } from 'react'
import { Copy, Eye, EyeOff, KeyRound, ShieldCheck } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { settingsApi } from '@/lib/settings-api'
import { copyToClipboard, parseApiErrors } from '@/lib/utils'
import type { OidcSettings as OidcSettingsModel } from '@/lib/types'
import '@/styles/account-page.css'

/**
 * V10.5 — Settings → Single sign-on (OIDC). The app-wide SSO provider that lets users sign in
 * through Authentik / Authelia / Keycloak. Mirrors the editable-SMTP model: DB-backed, the client
 * secret encrypted at rest and never returned (presence flag only); changes apply without a
 * restart. Off by default. Local password + 2FA login keep working alongside it.
 */
export function SsoSettings() {
  const [settings, setSettings] = useState<OidcSettingsModel | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    settingsApi.getOidcSettings()
      .then(setSettings)
      .catch(() => setSettings(null))
  }, [reload])

  return (
    <div className="account-page account-stack">
      <h1 className="text-2xl font-semibold">Single sign-on (OIDC)</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ShieldCheck className="h-5 w-5" /> OIDC / SSO provider
          </CardTitle>
          <CardDescription>
            Let users sign in through your identity provider — <strong>Authentik</strong>,
            <strong> Authelia</strong>, <strong>Keycloak</strong> or any OpenID Connect provider — instead of
            (or alongside) a local password. Stashboard uses the <strong>Authorization Code + PKCE</strong>
            flow; configure it once here and a <em>“Sign in with …”</em> button appears on the login page.
            Local password and two-factor sign-in keep working. Off by default.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <SsoSettingsForm
            key={`${settings?.enabled ?? false}|${settings?.issuer ?? ''}|${settings?.clientId ?? ''}|${settings?.hasClientSecret ?? false}|${settings?.redirectBaseUrl ?? ''}`}
            initial={settings}
            onSaved={() => setReload((v) => v + 1)}
          />

          <section className="host-shell-settings-section">
            <h3>How account linking works</h3>
            <p>
              An OIDC identity is matched to a Stashboard account by its <strong>verified email</strong>
              (your provider must mark the address as verified). The first SSO sign-in links the account and
              remembers the provider's stable user id, so a later email change in the provider doesn't break
              sign-in. If <strong>Allow OIDC registration</strong> is on, a first sign-in for an unknown
              verified email <strong>creates</strong> a new account; otherwise the user must already exist.
              A linked account can still use its password unless its owner turns local login off under
              <strong> Account → Single sign-on</strong>.
            </p>
          </section>
        </CardContent>
      </Card>
    </div>
  )
}

export function SsoSettingsForm({ initial, onSaved }: { initial: OidcSettingsModel | null; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(initial?.enabled ?? false)
  const [displayName, setDisplayName] = useState(initial?.displayName ?? '')
  const [issuer, setIssuer] = useState(initial?.issuer ?? '')
  const [clientId, setClientId] = useState(initial?.clientId ?? '')
  const [clientSecret, setClientSecret] = useState('')
  const [secretTouched, setSecretTouched] = useState(false)
  const [clearSecret, setClearSecret] = useState(false)
  const [revealSecret, setRevealSecret] = useState(false)
  const [scopes, setScopes] = useState(initial?.scopes ?? 'openid profile email')
  const [redirectBaseUrl, setRedirectBaseUrl] = useState(initial?.redirectBaseUrl ?? '')
  const [allowRegistration, setAllowRegistration] = useState(initial?.allowOidcRegistration ?? false)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const redirectUri = redirectBaseUrl ? `${redirectBaseUrl.replace(/\/+$/, '')}/oidc/callback` : ''

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setMessage(null)
    setFieldErrors({})
    try {
      await settingsApi.updateOidcSettings({
        enabled,
        displayName,
        issuer,
        clientId,
        clientSecret: clearSecret
          ? { action: 'Clear', value: null }
          : secretTouched
            ? { action: 'Set', value: clientSecret }
            : null,
        scopes,
        redirectBaseUrl,
        allowOidcRegistration: allowRegistration,
      })
      setMessage({ kind: 'ok', text: 'Single sign-on settings saved.' })
      onSaved()
    } catch (error: unknown) {
      const { fieldErrors: parsedFieldErrors, globalError } = parseApiErrors(error)
      setFieldErrors(parsedFieldErrors)
      setMessage({ kind: 'err', text: globalError ?? 'Failed to save single sign-on settings.' })
    } finally {
      setSaving(false)
    }
  }

  const testDiscovery = async () => {
    setTesting(true)
    setMessage(null)
    try {
      const result = await settingsApi.testOidcDiscovery()
      setMessage(result.ok
        ? { kind: 'ok', text: `Discovery succeeded. Authorization endpoint: ${result.authorizationEndpoint}` }
        : { kind: 'err', text: result.error ?? 'Discovery failed.' })
    } catch {
      setMessage({ kind: 'err', text: 'Discovery test failed. Save your issuer first, then try again.' })
    } finally {
      setTesting(false)
    }
  }

  const copyRedirect = () => {
    if (redirectUri) void copyToClipboard(redirectUri)
  }

  return (
    <form onSubmit={submit} className="account-form account-form-spaced">
      <label className="account-checkbox-label">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        Enable single sign-on (show the “Sign in with …” button)
      </label>

      <div className="account-field">
        <Label>Button label</Label>
        <Input
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          placeholder="Authentik"
          className={fieldErrors['displayName'] ? 'border-destructive' : ''}
        />
        <p className="host-shell-settings-note">Shown on the login button as “Sign in with {displayName || '…'}”.</p>
      </div>

      <div className="account-field">
        <Label>Issuer URL</Label>
        <Input
          value={issuer}
          onChange={(e) => setIssuer(e.target.value)}
          placeholder="https://auth.example.com/application/o/stashboard/"
          className={fieldErrors['issuer'] ? 'border-destructive' : ''}
        />
        <p className="host-shell-settings-note">
          The provider's base/issuer URL. Stashboard reads <code>{'{issuer}'}/.well-known/openid-configuration</code>
          {' '}for the endpoints and signing keys — no need to enter them by hand.
        </p>
      </div>

      <div className="account-field">
        <Label>Client ID</Label>
        <Input
          value={clientId}
          onChange={(e) => setClientId(e.target.value)}
          placeholder="stashboard"
          className={fieldErrors['clientId'] ? 'border-destructive' : ''}
        />
      </div>

      <div className="account-field">
        <Label>Client secret</Label>
        <div className="account-inline-row">
          <Input
            type={revealSecret ? 'text' : 'password'}
            value={clientSecret}
            disabled={clearSecret}
            onChange={(e) => { setClientSecret(e.target.value); setSecretTouched(true) }}
            placeholder={initial?.hasClientSecret ? '•••••••• (stored — leave blank to keep)' : '(leave blank for a public / PKCE-only client)'}
            className={fieldErrors['clientSecret'] ? 'account-input-with-icon border-destructive' : 'account-input-with-icon'}
          />
          <button type="button" className="account-icon-btn" onClick={() => setRevealSecret((v) => !v)}>
            {revealSecret ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        </div>
        {initial?.hasClientSecret && (
          <label className="account-checkbox-label">
            <input type="checkbox" checked={clearSecret} onChange={(e) => setClearSecret(e.target.checked)} />
            Remove the stored secret (make this a public / PKCE-only client)
          </label>
        )}
      </div>

      <div className="account-field">
        <Label>Scopes</Label>
        <Input value={scopes} onChange={(e) => setScopes(e.target.value)} placeholder="openid profile email" />
        <p className="host-shell-settings-note">Space-separated. Must include <code>openid</code>; <code>email</code> is required for account linking.</p>
      </div>

      <div className="account-field">
        <Label>Redirect base URL</Label>
        <Input
          value={redirectBaseUrl}
          onChange={(e) => setRedirectBaseUrl(e.target.value)}
          placeholder="https://stashboard.example.com"
          className={fieldErrors['redirectBaseUrl'] ? 'border-destructive' : ''}
        />
        <p className="host-shell-settings-note">
          The public URL of this Stashboard. Register this exact <strong>redirect URI</strong> at your provider:
        </p>
        {redirectUri && (
          <div className="account-inline-row">
            <Input readOnly value={redirectUri} className="account-input-with-icon" />
            <button type="button" className="account-icon-btn" onClick={copyRedirect} title="Copy redirect URI">
              <Copy className="h-4 w-4" />
            </button>
          </div>
        )}
      </div>

      <label className="account-checkbox-label">
        <input type="checkbox" checked={allowRegistration} onChange={(e) => setAllowRegistration(e.target.checked)} />
        Allow OIDC registration (create a new account on first sign-in for an unknown verified email)
      </label>

      {message && <p className={message.kind === 'ok' ? 'account-form-success' : 'account-form-error'}>{message.text}</p>}

      <div className="account-inline-row">
        <Button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save SSO settings'}</Button>
        <Button type="button" variant="outline" onClick={testDiscovery} disabled={testing}>
          <KeyRound className="h-4 w-4" /> {testing ? 'Testing…' : 'Test discovery'}
        </Button>
      </div>
    </form>
  )
}
