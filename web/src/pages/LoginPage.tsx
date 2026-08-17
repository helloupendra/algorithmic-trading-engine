import { useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setIsSubmitting(true)

    try {
      const me = await login(userNameOrEmail.trim(), password)

      // Send the user back where they were headed, or to their role's home.
      const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname
      navigate(from ?? (me.role === 'Admin' ? '/admin' : '/trader'), { replace: true })
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : 'Could not reach the API. Is it running on port 5025?',
      )
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="auth">
      <form className="auth__card" onSubmit={handleSubmit}>
        <div className="auth__brand">
          <span aria-hidden="true">▲</span> AlgoTrading
        </div>
        <h1 className="auth__title">Sign in</h1>
        <p className="auth__subtitle">Admin and trader access to the trading platform.</p>

        {error && (
          <div className="alert alert--error" role="alert">
            {error}
          </div>
        )}

        <label className="field">
          <span className="field__label">Username or email</span>
          <input
            className="field__input"
            value={userNameOrEmail}
            onChange={(e) => setUserNameOrEmail(e.target.value)}
            autoComplete="username"
            required
            autoFocus
          />
        </label>

        <label className="field">
          <span className="field__label">Password</span>
          <input
            className="field__input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>

        <button type="submit" className="btn btn--primary btn--block" disabled={isSubmitting}>
          {isSubmitting ? 'Signing in…' : 'Sign in'}
        </button>

        <p className="auth__hint">
          Accounts are created by an administrator. The first admin password is printed
          to the API console on first start.
        </p>
      </form>
    </div>
  )
}
