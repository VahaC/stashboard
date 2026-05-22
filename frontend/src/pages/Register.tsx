import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { accountApi } from '@/lib/account-api'
import { useAuthStore } from '@/lib/auth-store'
import { parseApiErrors } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

export function Register() {
  const nav = useNavigate()
  const setSession = useAuthStore((s) => s.setSession)
  const [email, setEmail] = useState('')
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
      const resp = await api.post('/api/auth/register', { email, password })
      setSession(resp.data)
      // Fire-and-forget — backend always returns 204 to avoid leaking which addresses exist.
      accountApi.resendConfirmation(email).catch(() => undefined)
      nav('/account')
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
            <CardTitle>Create account</CardTitle>
          </div>
          <CardDescription>Password: 8+ chars, 1 digit, 1 symbol</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} className="auth-form">
            <div className="auth-field">
              <Label htmlFor="email">Email</Label>
              <Input
                id="email"
                type="email"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className={fieldErrors['email'] ? 'border-destructive' : ''}
              />
              {fieldErrors['email'] && <p className="auth-field-error">{fieldErrors['email']}</p>}
            </div>
            <div className="auth-field">
              <Label htmlFor="password">Password</Label>
              <Input
                id="password"
                type="password"
                required
                minLength={8}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className={fieldErrors['password'] ? 'border-destructive' : ''}
              />
              {fieldErrors['password'] && <p className="auth-field-error">{fieldErrors['password']}</p>}
            </div>
            {error && <p className="auth-error">{error}</p>}
            <Button type="submit" className="auth-button-full" disabled={loading}>
              {loading ? 'Creating…' : 'Create account'}
            </Button>
            <p className="auth-links">
              Have an account? <Link to="/login" className="auth-link">Sign in</Link>
            </p>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
