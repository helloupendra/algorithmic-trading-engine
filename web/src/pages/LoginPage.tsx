/**
 * Sign-in, v2 design. Logic is unchanged — username/password to the API, with
 * dev-only one-click logins sourced from web/.env.local (never shipped in a
 * production build).
 */

import { useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { IconLogo } from '../components/icons'

const DEV_LOGINS = import.meta.env.DEV
  ? (
      [
        {
          label: 'Admin',
          hint: 'full access — users, kill switch, ingestion',
          user: import.meta.env.VITE_DEV_ADMIN_USER as string | undefined,
          pass: import.meta.env.VITE_DEV_ADMIN_PASS as string | undefined,
        },
        {
          label: 'Trader',
          hint: 'trading screens only — no admin area',
          user: import.meta.env.VITE_DEV_TRADER_USER as string | undefined,
          pass: import.meta.env.VITE_DEV_TRADER_PASS as string | undefined,
        },
      ] as const
    ).filter((l) => l.user && l.pass)
  : []

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)

  async function signIn(name: string, pass: string) {
    setError(null)
    setIsSubmitting(true)
    try {
      const me = await login(name, pass)
      // RequireAuth stores the full Location; keep search and hash so deep links
      // such as /trader/charts?symbol=NIFTY survive the sign-in detour.
      const from = (location.state as { from?: { pathname: string; search?: string; hash?: string } } | null)?.from
      const target = from ? `${from.pathname}${from.search ?? ''}${from.hash ?? ''}` : null
      navigate(target ?? (me.role === 'Admin' ? '/admin' : '/trader'), { replace: true })
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

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void signIn(userNameOrEmail.trim(), password)
  }

  return (
    <div className="login">
      <div>
        <div className="login__card">
          <div className="login__brand">
            <span className="shell__brand-mark" aria-hidden="true">
              <IconLogo />
            </span>
            AlgoTrading Console
          </div>

          <h1 className="login__title">Sign in</h1>
          <p className="login__sub">Live data, strategies and risk — one console.</p>

          {error && (
            <div className="alert alert--error" role="alert" style={{ marginBottom: 14 }}>
              {error}
            </div>
          )}

          <form onSubmit={handleSubmit} className="login__form">
            <label className="field">
              <span className="field__label">Username or email</span>
              <input
                className="field__input"
                value={userNameOrEmail}
                onChange={(e) => setUserNameOrEmail(e.target.value)}
                autoComplete="username"
                placeholder="you@example.com"
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
                placeholder="••••••••••••"
                required
              />
            </label>

            <button type="submit" className="btn btn--primary btn--block" disabled={isSubmitting}>
              {isSubmitting ? 'Signing in…' : 'Sign in'}
            </button>
          </form>

          <p className="login__hint">
            Accounts are issued by an administrator — there is no public sign-up.
          </p>

          {DEV_LOGINS.length > 0 && (
            <div className="login__dev">
              <div className="login__dev-label">Dev quick sign-in</div>
              <div className="login__dev-row">
                {DEV_LOGINS.map((l) => (
                  <button
                    key={l.label}
                    type="button"
                    className="login__dev-btn"
                    disabled={isSubmitting}
                    title={l.hint}
                    onClick={() => void signIn(l.user!, l.pass!)}
                  >
                    <b>{l.label}</b>
                    <span>{l.user}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>

        <p className="login__foot">
          Trading involves financial risk. Validate every strategy on paper first.
        </p>
      </div>
    </div>
  )
}
