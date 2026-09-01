/**
 * Sign-in page, in the same visual language as the landing page: aurora glow
 * field, glass card, gradient CTA. Logic is unchanged — username/password to
 * the API, with dev-only one-click logins from web/.env.local.
 */

import { useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'

/**
 * One-click sign-in shortcuts for local development.
 *
 * Credentials come from web/.env.local (gitignored) and the section renders
 * only on the Vite dev server — a production build never ships either the
 * buttons or the credentials.
 */
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

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void signIn(userNameOrEmail.trim(), password)
  }

  return (
    <div className="lg">
      <div className="lp-bg" aria-hidden="true">
        <span className="lp-orb lp-orb--blue" />
        <span className="lp-orb lp-orb--green" />
        <span className="lp-grid" />
      </div>

      <Link to="/" className="lg-home">
        <span aria-hidden="true">←</span> Back to home
      </Link>

      <div className="lg-card">
        <div className="lg-card__glow" aria-hidden="true" />

        <Link to="/" className="lp-brand lg-brand">
          <span className="lp-brand__mark">▲</span> AlgoTrading
        </Link>

        <h1 className="lg-title">Welcome back</h1>
        <p className="lg-sub">Sign in to your trading console.</p>

        {error && (
          <div className="alert alert--error" role="alert">
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit} className="lg-form">
          <label className="field">
            <span className="field__label">Username or email</span>
            <input
              className="field__input lg-input"
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
              className="field__input lg-input"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              placeholder="••••••••••••"
              required
            />
          </label>

          <button type="submit" className="lp-cta lg-submit" disabled={isSubmitting}>
            {isSubmitting ? 'Signing in…' : 'Sign in'} <span aria-hidden="true">→</span>
          </button>
        </form>

        <p className="lg-hint">
          Accounts are issued by an administrator — there is no public sign-up.
        </p>

        {DEV_LOGINS.length > 0 && (
          <div className="lg-dev">
            <div className="lg-dev__label">
              <span /> Dev quick sign-in <span />
            </div>
            <div className="lg-dev__row">
              {DEV_LOGINS.map((l) => (
                <button
                  key={l.label}
                  type="button"
                  className="lg-dev__btn"
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

      <p className="lg-foot">
        Trading involves financial risk. Validate every strategy on paper first.
      </p>
    </div>
  )
}
