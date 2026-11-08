/**
 * Sign-in ("/login"), sprout v1 (8 Nov 2026).
 *
 * One composition, like the homepage it comes from: on the left the desk
 * before the open (a headline and a small, synthetic session of candles), on
 * the right the form. No WebGL: the candles are inline SVG, and they answer
 * the form the way the old engine did. They wake as the fields are filled in
 * (a little for focus, the rest for what has been typed), brighten fully on
 * submit while a candle loader replaces the form, and jolt on a failed
 * sign-in, which brings the form back with the server's message and returns
 * focus to the first field.
 *
 * Top right, the market's state from the API's exchange calendar (open to
 * anyone): open until when, or closed (for which holiday) until when. Until
 * it answers, the NSE cash hours, which are true at any time.
 * Username and password only: accounts are issued by an administrator.
 */

import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { useMarketSession } from '../lib/queries'
import { CASH_HOURS, istNow, marketLine } from '../lib/marketLine'
import { homeFor, returnTarget } from '../lib/modules'
import { IconLogo } from '../components/icons'
import { CandleBars } from '../components/CandleLoader'
import './login/signin.css'

/** A synthetic session for the left side: [x, wick top, wick bottom, body top, body height, up]. */
const CANDLES: readonly [number, number, number, number, number, boolean][] = [
  [8, 65, 98, 75, 15, true], [24, 68, 100, 77, 13, true], [40, 72, 102, 79, 13, false], [56, 56, 90, 66, 14, true],
  [72, 45, 75, 54, 15, true], [88, 49, 81, 57, 13, false], [104, 48, 84, 58, 15, true], [120, 61, 92, 68, 15, false],
  [136, 45, 76, 55, 13, true], [152, 31, 65, 40, 16, true], [168, 32, 65, 39, 18, false], [184, 31, 56, 37, 11, true],
  [200, 18, 48, 25, 16, true], [216, 21, 54, 28, 18, false], [232, 18, 49, 25, 15, true], [248, 29, 58, 36, 14, true],
  [264, 32, 67, 40, 17, false], [280, 38, 68, 45, 15, true], [296, 42, 76, 49, 18, false], [312, 36, 67, 44, 13, true],
  [328, 27, 56, 35, 13, true], [344, 31, 65, 39, 16, false], [360, 40, 70, 47, 15, true], [376, 47, 75, 54, 13, false],
  [392, 38, 64, 45, 11, true], [408, 34, 62, 41, 13, true], [424, 37, 70, 44, 17, false], [440, 43, 70, 50, 12, true],
  [456, 47, 77, 55, 13, false], [472, 39, 71, 47, 15, true], [488, 30, 62, 38, 15, true], [504, 36, 66, 43, 15, false],
]

function SessionCandles() {
  return (
    <svg className="signin__candles" viewBox="0 0 520 132" role="img" aria-labelledby="signin-candles-title">
      <title id="signin-candles-title">Synthetic NIFTY session, 09:15 to 13:45 IST. Not a quote.</title>
      <path className="signin__grid" d="M0 25H520M0 64H520M0 103H520" />
      {CANDLES.map(([x, top, bottom, bodyTop, bodyH, up], i) => (
        <g key={x} className={up ? 'signin__up' : 'signin__down'} style={{ ['--i' as string]: i }}>
          <path d={`M${x} ${top}V${bottom}`} />
          <rect x={x - 4} y={bodyTop} width="8" height={bodyH} rx="1" />
        </g>
      ))}
      <path className="signin__last" d="M0 62H520" />
      <g className="signin__times">
        <text x="0" y="128">09:15</text>
        <text x="150" y="128">10:45</text>
        <text x="305" y="128">12:15</text>
        <text x="520" y="128" textAnchor="end">13:45 IST</text>
      </g>
    </svg>
  )
}

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const session = useMarketSession('NSE', 'FO')
  const market = session.data ? marketLine(session.data) : null

  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [focused, setFocused] = useState(false)
  // Bumped on each failure so the jolt replays even for the same message twice.
  const [jolts, setJolts] = useState(0)
  const userRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => {
    const previous = document.title
    document.title = 'Sign in — OpenFNO'
    return () => {
      document.title = previous
    }
  }, [])

  // The candles wake as the form is filled in: a little for focus, the rest for what has been typed.
  const wake = Math.min(1, (focused ? 0.22 : 0.08) + (userNameOrEmail.length + password.length) / 18)

  async function signIn(name: string, pass: string) {
    setError(null)
    setIsSubmitting(true)
    try {
      const me = await login(name, pass)
      // RequireAuth (and the Ira app's gate) store the full Location; keep search
      // and hash so deep links such as /markets/chart?symbol=NIFTY survive the
      // sign-in detour, and the phone app comes back to /ira.
      // Otherwise their home: Today for an admin, the Desk for a trader.
      navigate(returnTarget(location.state) ?? homeFor(me), { replace: true })
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
      setJolts((n) => n + 1)
      // The submit button was disabled while focused, which drops focus to the
      // body; put it back where the correction starts.
      window.setTimeout(() => userRef.current?.focus(), 0)
    }
    // On success the loader stays up until the console route has taken over.
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void signIn(userNameOrEmail.trim(), password)
  }

  const opening = isSubmitting && !error

  return (
    <div className="signin">
      <header className="signin__top">
        {/* A full load: "/" is the static homepage, not a route of this app. */}
        <a className="signin__brand" href="/" aria-label="OpenFNO homepage">
          <IconLogo className="signin__mark" />
          <span>OpenFNO</span>
        </a>
        <p className="signin__clock" title={market ? `Time in India now: ${istNow()}` : 'NSE cash market hours, Monday to Friday'}>
          <span>
            <i className={market?.open ? 'is-open' : ''} aria-hidden="true" />
            {market?.text ?? CASH_HOURS}
          </span>
        </p>
      </header>

      <main className="signin__main">
        <section
          className={`signin__intro${opening ? ' is-opening' : ''}`}
          aria-labelledby="signin-title"
          style={{ ['--wake' as string]: opening ? 1 : wake }}
        >
          <p className="signin__eyebrow">The desk, before the open</p>
          <h1 id="signin-title">
            Sign in.
            <br />
            <em>The engine wakes.</em>
          </h1>
          <figure key={jolts} className={`signin__market${jolts ? ' is-jolted' : ''}`}>
            <div className="signin__market-head">
              <span>
                <b>NIFTY 50</b>
                <small>NSE · session record</small>
              </span>
              <span className="signin__price num">25,031.40</span>
            </div>
            <SessionCandles />
            <figcaption>
              <i aria-hidden="true" /> Synthetic · not a quote
            </figcaption>
          </figure>
          <p className="signin__principle">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6zM8 12l3 3 5-6" />
            </svg>
            Paper trades. Real records. Every P&amp;L after charges.
          </p>
        </section>

        <section className="signin__card" aria-labelledby="signin-form-title">
          <div className="signin__card-top">
            <span>Your workspace</span>
            <span className="signin__paper">
              <i aria-hidden="true" /> Paper only
            </span>
          </div>
          <h2 id="signin-form-title">Sign in</h2>

          {opening ? (
            <div className="signin__opening" role="status">
              <CandleBars />
              <p>Opening the console…</p>
            </div>
          ) : (
            <form onSubmit={handleSubmit} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)} noValidate={false}>
              {error && (
                <p className="signin__error" role="alert">
                  {error}
                </p>
              )}
              <div className="signin__field">
                <label htmlFor="signin-user">Username or email</label>
                <input
                  id="signin-user"
                  ref={userRef}
                  value={userNameOrEmail}
                  onChange={(e) => setUserNameOrEmail(e.target.value)}
                  autoComplete="username"
                  autoCapitalize="none"
                  spellCheck={false}
                  placeholder="you@example.com"
                  required
                  autoFocus
                />
              </div>
              <div className="signin__field">
                <label htmlFor="signin-password">Password</label>
                <div className="signin__password">
                  <input
                    id="signin-password"
                    type={showPassword ? 'text' : 'password'}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="current-password"
                    required
                  />
                  <button
                    type="button"
                    className="signin__reveal"
                    aria-label={showPassword ? 'Hide password' : 'Show password'}
                    aria-pressed={showPassword}
                    onClick={() => setShowPassword((v) => !v)}
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                      <path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z" />
                      <circle cx="12" cy="12" r="3" />
                      {showPassword && <path d="M4 4l16 16" />}
                    </svg>
                  </button>
                </div>
              </div>
              <button type="submit" className="signin__submit" disabled={isSubmitting}>
                Sign in <span aria-hidden="true">→</span>
              </button>
              <p className="signin__hint">
                Accounts are issued by an administrator — there is no public <span className="nowrap">sign-up.</span>
              </p>
            </form>
          )}
        </section>
      </main>

      <footer className="signin__foot">
        <p>Paper trading only. Nothing places a real order.</p>
        <p>
          <a href="https://github.com/helloupendra/algorithmic-trading-engine" target="_blank" rel="noopener noreferrer">
            Source-available
          </a>{' '}
          · PolyForm Noncommercial 1.0.0
        </p>
      </footer>
    </div>
  )
}
