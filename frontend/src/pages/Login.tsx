import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { useAuthStore } from '@/lib/auth-store'
import { parseApiErrors } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

export function Login() {
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
      const resp = await api.post('/api/auth/login', { email, password })
      setSession(resp.data)
      nav('/')
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? 'Invalid email or password')
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
            <CardTitle>Sign in</CardTitle>
          </div>
          <CardDescription>Stashboard self-hosted dashboard</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submit} className="auth-form">
            <div className="auth-field">
              <Label htmlFor="email">Email</Label>
              <Input id="email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} className={fieldErrors['email'] ? 'border-destructive' : ''} />
              {fieldErrors['email'] && <p className="auth-field-error">{fieldErrors['email']}</p>}
            </div>
            <div className="auth-field">
              <Label htmlFor="password">Password</Label>
              <Input id="password" type="password" required value={password} onChange={(e) => setPassword(e.target.value)} className={fieldErrors['password'] ? 'border-destructive' : ''} />
              {fieldErrors['password'] && <p className="auth-field-error">{fieldErrors['password']}</p>}
            </div>
            {error && <p className="auth-error">{error}</p>}
            <Button type="submit" className="auth-button-full" disabled={loading}>
              {loading ? 'Signing in…' : 'Sign in'}
            </Button>
            <div className="auth-links auth-links-stack">
              <p>
                <Link to="/forgot-password" className="auth-link">Forgot password?</Link>
              </p>
              <p>
                No account? <Link to="/register" className="auth-link">Register</Link>
              </p>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
