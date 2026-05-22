import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { accountApi } from '@/lib/account-api'
import { parseApiErrors } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

export function ResetPassword() {
  const nav = useNavigate()
  const [params] = useSearchParams()
  const email = params.get('email') ?? ''
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setFieldErrors({})
    setLoading(true)
    try {
      await accountApi.resetPassword(email, token, password)
      nav('/login?reset=1')
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError)
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="dark-glow auth-page">
      <Card className="auth-card">
        <CardHeader>
          <div className="auth-brand">
            <img src={logo} alt="" className="auth-logo" />
            <CardTitle>Reset password</CardTitle>
          </div>
          <CardDescription>Choose a new password for {email || 'your account'}.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} className="auth-form">
            <div className="auth-field">
              <Label htmlFor="password">New password</Label>
              <Input
                id="password"
                type="password"
                required
                minLength={8}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className={fieldErrors['newpassword'] || fieldErrors['password'] ? 'border-destructive' : ''}
              />
              {(fieldErrors['newpassword'] ?? fieldErrors['password']) && (
                <p className="auth-field-error">{fieldErrors['newpassword'] ?? fieldErrors['password']}</p>
              )}
            </div>
            {error && <p className="auth-error">{error}</p>}
            <Button type="submit" className="auth-button-full" disabled={loading || !email || !token}>
              {loading ? 'Resetting…' : 'Reset password'}
            </Button>
            <p className="auth-links">
              <Link to="/login" className="auth-link">Back to sign in</Link>
            </p>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
