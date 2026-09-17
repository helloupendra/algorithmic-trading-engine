/**
 * Sign-in.
 *
 * The page a trading desk opens with. The left half is the session this page is
 * about to start — the market clock in IST, what a session brings up, and a tape
 * printing under it — and the right half is the form. The field behind both is
 * the homepage's (components/AuroraScene), so arriving here from "/" feels like
 * walking into the same room.
 *
 * The clock is the visitor's own, formatted in IST; the session line is computed
 * from the standard NSE cash timings and says "holidays aside" because this page
 * does not load the calendar. Nothing here pretends to be live market data.
 *
 * Username and password only. There is no public sign-up and no shortcut:
 * accounts are issued by an administrator.
 */

import { useEffect, useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { IconLogo } from '../components/icons'
import { AuroraCanvas } from '../components/AuroraScene'
import { CandleBars, CandleLoader } from '../components/CandleLoader'
import './landing.css'

/** The IST clock, and whether the cash session is open by the standard timings. */
function useMarketClock() {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), 1000)
    return () => window.clearInterval(id)
  }, [])

  const time = new Intl.DateTimeFormat('en-IN', {
    timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(now)
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Kolkata', weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false,
  }).formatToParts(now)
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? ''
  const weekday = get('weekday')
  const minutes = Number(get('hour')) * 60 + Number(get('minute'))
  const weekend = weekday === 'Sat' || weekday === 'Sun'
  // 09:15 → 15:30 IST, the NSE cash session. The calendar lives in the console,
  // not on this page, which is why the label says "holidays aside".
  const open = !weekend && minutes >= 555 && minutes < 930
  return { time, open }
}

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const clock = useMarketClock()

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
      setIsSubmitting(false)
    }
    // On success the loader stays up until the console route has taken over.
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void signIn(userNameOrEmail.trim(), password)
  }

  return (
    <div className="login">
      <AuroraCanvas className="login__light" warm={1.6} />

      {isSubmitting && !error && <CandleLoader label="Opening the console" />}

      <header className="login__top">
        <Link className="login__brand" to="/">
          <span className="login__mark" aria-hidden="true"><IconLogo /></span>
          <span className="login__wordmark">
            <span className="login__word">open<b>fno</b></span>
            <span className="login__tag">Open-source F&amp;O desk</span>
          </span>
        </Link>
        <div className="login__clock">
          <span className={`login__state${clock.open ? ' is-open' : ''}`}>
            <i aria-hidden="true" />{clock.open ? 'Market open' : 'Market closed'}
          </span>
          <span className="login__time">{clock.time} IST</span>
          <span className="login__note">NSE cash timings, holidays aside</span>
        </div>
      </header>

      <div className="login__grid">
        <section className="login__pitch">
          <h1 className="login__lead">
            The desk,<br />
            <em>before the open.</em>
          </h1>

          <div className="term">
            <div className="term__bar"><i /><i /><i /><span>session · what signing in brings up</span></div>
            <pre className="term__body"><code>
              <span className="term__line"><b>feeds</b>      four vendors, one store — ticks, bars, chain</span>
              <span className="term__line"><b>history</b>    five years of index candles, ±10 strikes of options</span>
              <span className="term__line"><b>strategies</b> 25 in the catalogue, or one you write here</span>
              <span className="term__line"><b>risk</b>       leg → group → day, guarded every 3 seconds</span>
              <span className="term__line term__line--cue"><b>execution</b>  paper, on live ticks</span>
            </code></pre>
            <CandleBars className="term__tape" />
          </div>
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
