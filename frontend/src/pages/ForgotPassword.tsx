import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { accountApi } from '@/lib/account-api'
import { parseApiErrors } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import '@/styles/auth-pages.css'

export function ForgotPassword() {
  const [email, setEmail] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [loading, setLoading] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setFieldErrors({})
    setLoading(true)
    try {
      await accountApi.forgotPassword(email)
      setDone(true)
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? 'Something went wrong. Please try again.')
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
            <CardTitle>Forgot password</CardTitle>
          </div>
          <CardDescription>We'll send a reset link if the address is registered.</CardDescription>
        </CardHeader>
        <CardContent>
          {done ? (
            <div className="auth-form">
              <p className="auth-message">If an account exists for <b>{email}</b>, a reset link has been sent. Check your inbox.</p>
              <Link to="/login" className="auth-link auth-message">Back to sign in</Link>
            </div>
          ) : (
            <form onSubmit={submit} className="auth-form">
              <div className="auth-field">
                <Label htmlFor="email">Email</Label>
                <Input id="email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} className={fieldErrors['email'] ? 'border-destructive' : ''} />
                {fieldErrors['email'] && <p className="auth-field-error">{fieldErrors['email']}</p>}
              </div>
              {error && <p className="auth-error">{error}</p>}
              <Button type="submit" className="auth-button-full" disabled={loading}>
                {loading ? 'Sending…' : 'Send reset link'}
              </Button>
              <p className="auth-links">
                <Link to="/login" className="auth-link">Back to sign in</Link>
              </p>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
