import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { accountApi } from '@/lib/account-api'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

type State = 'pending' | 'ok' | 'fail'

export function ConfirmEmail() {
  const [params] = useSearchParams()
  const email = params.get('email') ?? ''
  const token = params.get('token') ?? ''
  const [state, setState] = useState<State>(() => (email && token ? 'pending' : 'fail'))

  useEffect(() => {
    if (!email || !token) return
    accountApi.confirmEmail(email, token).then(
      () => setState('ok'),
      () => setState('fail'),
    )
  }, [email, token])

  return (
    <div className="dark-glow auth-page">
      <Card className="auth-card">
        <CardHeader>
          <div className="auth-brand">
            <img src={logo} alt="" className="auth-logo" />
            <CardTitle>
              {state === 'pending' && 'Confirming…'}
              {state === 'ok' && 'Email confirmed'}
              {state === 'fail' && 'Confirmation failed'}
            </CardTitle>
          </div>
          <CardDescription>
            {state === 'pending' && 'Hold on a moment.'}
            {state === 'ok' && 'Your email is verified.'}
            {state === 'fail' && 'Link is invalid or expired.'}
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Link to="/login" className="auth-link auth-message">Back to sign in</Link>
        </CardContent>
      </Card>
    </div>
  )
}
