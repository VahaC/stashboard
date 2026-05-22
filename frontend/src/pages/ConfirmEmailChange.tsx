import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { accountApi } from '@/lib/account-api'
import { getApiErrorMessage } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

export function ConfirmEmailChange() {
  const nav = useNavigate()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [error, setError] = useState<string | null>(() => (token ? null : 'Missing token.'))

  useEffect(() => {
    if (!token) return
    accountApi.confirmEmailChange(token).then(
      () => nav('/account?emailChanged=1'),
      (e: unknown) => {
        setError(getApiErrorMessage(e, 'Confirmation failed.'))
      },
    )
  }, [token, nav])

  return (
    <div className="dark-glow auth-page">
      <Card className="auth-card">
        <CardHeader>
          <div className="auth-brand">
            <img src={logo} alt="" className="auth-logo" />
            <CardTitle>Confirming new email…</CardTitle>
          </div>
          <CardDescription>{error ?? 'One moment.'}</CardDescription>
        </CardHeader>
        <CardContent />
      </Card>
    </div>
  )
}
