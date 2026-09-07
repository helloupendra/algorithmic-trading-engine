/**
 * Sign-in. One composition: the strike ledger as a low shelf along the foot of
 * the page (see components/ChainScene — the option-chain poller's ring buffer,
 * quieter here and never moving under the pointer), a soft brand glow behind
 * the card, and nothing else competing with the form. The card carries the
 * contrast; the backdrop only has to say what this is.
 *
 * Username and password only. There is no public sign-up and no shortcut:
 * accounts are issued by an administrator.
 */

import { useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { IconLogo } from '../components/icons'
import { ChainCanvas } from '../components/ChainScene'

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)
  // The risk note; the shelf keeps its call tips below it.
  const footRef = useRef<HTMLParagraphElement | null>(null)

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
      // An ApiError carries a message the server chose to show a user ("wrong
      // password"). Anything else is the request never completing - a dropped
      // connection, a tunnel blip, the server restarting - and the operator on
      // a phone can do nothing with a port number. Name the port only where a
      // developer would see it.
      setError(
        err instanceof ApiError
          ? err.message
          : import.meta.env.DEV
            ? 'Could not reach the API. Is it running on port 5025?'
            : "Couldn't reach the server. Check your connection and try again.",
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
      <div className="login__pane">
        <div className="login__card">
          <Link className="login__brand" to="/">
            <span className="shell__brand-mark" aria-hidden="true">
              <IconLogo />
            </span>
            AlgoTrading Console
          </Link>

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
        </div>

        <p className="login__foot" ref={footRef}>
          Trading involves financial risk. Validate every strategy on paper first.
        </p>
      </div>

      {/* After the pane: the shelf measures the risk note in a layout effect,
          and refs attach in tree order. z-index puts it behind the card. */}
      <ChainCanvas variant="login" anchorRef={footRef} className="login__scene" />
    </div>
  )
}
