/**
 * Sign-in ("/login").
 *
 * The same engine as the homepage (components/MachineScene), standing on the
 * left of one continuous room, half asleep in its glass: the desk before the
 * open. The form is a sheet of glass on the right of the same room. On a narrow
 * screen the form takes the centre and the engine stands behind it.
 *
 * The page tells the world where the engine should stand by measuring the
 * empty side of its own layout, so the two never drift apart.
 *
 * The engine answers the form. It wakes as the fields are filled in; on submit
 * the lid lifts and the candle loader plays in place of the form; a failed
 * sign-in flashes the case red, drops the lid, brings the form back with the
 * server's message and returns focus to the first field.
 *
 * The clock top-right is the visitor's own, read in IST, with the NSE cash
 * timings ("holidays aside", because this page does not load the calendar).
 * Username and password only: accounts are issued by an administrator.
 */

import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { ApiError } from '../lib/api'
import { useMarketClock } from '../lib/marketClock'
import { liveSceneWanted } from '../lib/sceneMode'
import { IconLogo } from '../components/icons'
import { MachineScene } from '../components/MachineScene'
import { Still } from '../components/Still'
import { CandleBars } from '../components/CandleLoader'
import type { MachineHandle } from '../scene/machine'
import './landing.css'

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const clock = useMarketClock()

  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [focused, setFocused] = useState(false)
  const [live, setLive] = useState(() => liveSceneWanted())
  const machineRef = useRef<MachineHandle | null>(null)
  const userRef = useRef<HTMLInputElement | null>(null)
  const sideRef = useRef<HTMLDivElement | null>(null)

  /** Stand the engine in the middle of the side the form leaves free (or behind the form, when there is none). */
  const placeEngine = useCallback(() => {
    const side = sideRef.current
    const m = machineRef.current
    if (!side || !m) return
    const r = side.getBoundingClientRect()
    const free = r.width > 240
    const portrait = window.innerHeight > window.innerWidth * 1.15
    const cx = free ? (r.left + r.width / 2) / window.innerWidth - 0.5 : 0
    // Beside the form when there is a free side; under it on a phone; behind it otherwise.
    m.setScreenCentre(cx, free ? 0.12 : portrait ? 0.3 : 0.04)
  }, [])

  useEffect(() => {
    window.addEventListener('resize', placeEngine)
    return () => window.removeEventListener('resize', placeEngine)
  }, [placeEngine])

  useEffect(() => {
    const previous = document.title
    document.title = 'Sign in — OpenFNO'
    return () => {
      document.title = previous
    }
  }, [])

  // The engine wakes as the form is filled in: a little for focus, the rest for what has been typed.
  const energy = Math.min(1, (focused ? 0.22 : 0.08) + (userNameOrEmail.length + password.length) / 18)
  useEffect(() => {
    machineRef.current?.setEnergy(energy)
  }, [energy])

  const onReady = useCallback((m: MachineHandle) => {
    machineRef.current = m
    m.setEnergy(0.08)
    placeEngine()
  }, [placeEngine])

  async function signIn(name: string, pass: string) {
    setError(null)
    setIsSubmitting(true)
    machineRef.current?.fire('open')
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
      machineRef.current?.fire('error')
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
    <div className="login">
      {live
        ? <MachineScene mode="login" onReady={onReady} onUnavailable={() => setLive(false)} />
        : <Still pose="login" className="login__still" />}

      <header className="login__top">
        <Link className="brand" to="/" aria-label="OpenFNO home">
          <span className="brand__mark"><IconLogo /></span>
          <span className="brand__word">openfno</span>
        </Link>
        <span className={`market${clock.open ? ' is-open' : ''}`}>
          <i aria-hidden="true" />NSE {clock.open ? 'open' : 'closed'} · {clock.time} IST
          <small>cash timings, holidays aside</small>
        </span>
      </header>

      <main className="login__main">
        <div className="login__side" ref={sideRef}>
          <p className="eyebrow"><i aria-hidden="true" />The desk, before the open</p>
          <h1 className="login__lead">Sign in.<br /><em>The engine wakes.</em></h1>
        </div>

        <div className="login__form">
        <div className={`card${opening ? ' is-opening' : ''}${error ? ' is-error' : ''}`}>
          <div className="card__form" inert={opening}>
            <h2 className="card__title">Sign in</h2>

            {error && <div className="card__alert" role="alert">{error}</div>}

            <form onSubmit={handleSubmit} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
              <label className="field">
                <span className="field__label">Username or email</span>
                <input
                  ref={userRef}
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
              <button type="submit" className="cta cta--solid cta--block" disabled={isSubmitting}>
                {isSubmitting ? 'Opening…' : <>Sign in <span className="arrow" aria-hidden="true">→</span></>}
              </button>
            </form>

            <p className="card__hint">Accounts are issued by an administrator — there is no public sign-up.</p>
          </div>

          {opening && (
            <div className="card__opening" role="status">
              <CandleBars />
              <p>Opening the console<span className="loader__dots" aria-hidden="true">…</span></p>
            </div>
          )}
        </div>
        <p className="login__risk">Trading involves financial risk. Validate every strategy on paper first.</p>
        </div>
      </main>
    </div>
  )
}
