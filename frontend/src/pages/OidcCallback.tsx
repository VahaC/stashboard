import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { useAuthStore } from '@/lib/auth-store'
import { parseApiErrors } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

/**
 * V10.5 — the OIDC redirect lands here. The provider sends `code` + `state` (or an `error`); we
 * POST them to the backend, which exchanges the code and returns the normal session, then we land
 * the user on the dashboard exactly like a password sign-in.
 */
export function OidcCallback() {
  const nav = useNavigate()
  const [params] = useSearchParams()
  const setSession = useAuthStore((s) => s.setSession)
  const [exchangeError, setExchangeError] = useState<string | null>(null)

  const providerError = params.get('error')
  const code = params.get('code')
  const state = params.get('state')

  // Parameter problems are derived synchronously from the URL — no setState needed for them.
  const paramError = providerError
    ? (params.get('error_description') || `Your provider returned an error: ${providerError}`)
    : (!code || !state ? 'The sign-in response was missing required parameters.' : null)

  // React 19 StrictMode double-invokes effects in dev; the state is single-use server-side, so a
  // second exchange would fail. Guard so we only ever post once.
  const exchanged = useRef(false)

  useEffect(() => {
    if (paramError || exchanged.current) return
    exchanged.current = true

    api.post('/api/auth/oidc/callback', { code, state })
      .then((resp) => {
        setSession(resp.data)
        nav('/', { replace: true })
      })
      .catch((e: unknown) => {
        const { globalError } = parseApiErrors(e)
        setExchangeError(globalError ?? 'Single sign-on failed. Please try again.')
      })
  }, [paramError, code, state, nav, setSession])

  const error = paramError ?? exchangeError

  return (
    <div className="dark-glow auth-page">
      <Card className="auth-card">
        <CardHeader>
          <div className="auth-brand">
            <img src={logo} alt="" className="auth-logo" />
            <CardTitle>{error ? 'Sign-in failed' : 'Signing you in…'}</CardTitle>
          </div>
          <CardDescription>
            {error ? 'Single sign-on could not complete.' : 'Completing single sign-on with your provider.'}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {error ? (
            <div className="auth-form">
              <p className="auth-error">{error}</p>
              <div className="auth-links auth-links-stack">
                <p><Link to="/login" className="auth-link">Back to sign in</Link></p>
              </div>
            </div>
          ) : (
            <p className="auth-message">Please wait…</p>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
