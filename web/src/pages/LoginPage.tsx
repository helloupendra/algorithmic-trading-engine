/**
 * Sign-in.
 *
 * One composition, the homepage's: the same field of colour (components/
 * AuroraScene) lighting the page, the product's own claim on the left, and the
 * form in glass on the right. Narrow screens keep the form and the three lines
 * that say what this is — someone opening this on a phone at 09:14 came for the
 * form, not for the picture.
 *
 * Username and password only. There is no public sign-up and no shortcut:
 * accounts are issued by an administrator.
 */

import { useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { IconLogo } from '../components/icons'
import { AuroraCanvas } from '../components/AuroraScene'
import './landing.css'

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
      <AuroraCanvas className="login__light" warm={1.6} />

      <div className="login__grid">
        <section className="login__pitch">
          <Link className="login__brand" to="/">
            <span className="login__mark" aria-hidden="true"><IconLogo /></span>
            <span className="login__wordmark">
              <span className="login__word">open<b>fno</b></span>
              <span className="login__tag">Open-source F&amp;O desk</span>
            </span>
          </Link>

          <h1 className="login__lead">
            The desk,<br />
            <em>before the open.</em>
          </h1>

          <ul className="login__points">
            <li><b>Live and replay, one contract.</b> The runner and the backtester feed a strategy the same shapes.</li>
            <li><b>Coverage first.</b> Every picker is built from what is actually stored — no empty range to ask for.</li>
            <li><b>Nothing fails silently.</b> A stale feed, a skipped entry or a stopped run says so, with a reason.</li>
          </ul>

          <figure className="login__shot">
            <img
              src="/shots/option-chain.webp"
              width={1600}
              height={939}
              loading="lazy"
              decoding="async"
              alt="The console's option chain: calls on the left, puts on the right, strikes down the middle."
            />
          </figure>
        </section>

        <div className="login__pane">
          <div className="login__card">
            <h2 className="login__title">Sign in</h2>
            <p className="login__sub">Live data, strategies, backtests and risk — one console.</p>

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

          <p className="login__foot">
            Trading involves financial risk. Validate every strategy on paper first.
          </p>
        </div>
      </div>
    </div>
  )
}
