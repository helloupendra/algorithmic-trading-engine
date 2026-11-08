/**
 * Accepting an invitation — the only way onto this platform without an admin
 * typing your password for them.
 *
 * Public by necessity: the person arriving here has no account yet. That is safe
 * because of what an accepted invite creates — an account holding nothing, which
 * can sign in and do nothing until an admin grants it something.
 */

import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { IconLogo } from '../components/icons'
import './login/signin.css'
import type { InvitePreview } from '../lib/types'

export function AcceptInvitePage() {
  const { token = '' } = useParams()
  const navigate = useNavigate()
  const { login } = useAuth()

  const [preview, setPreview] = useState<InvitePreview | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false

    api
      .get<InvitePreview>(`/api/Invites/${encodeURIComponent(token)}`)
      .then((data) => {
        if (cancelled) return
        setPreview(data)
        setUserName(data.suggestedUserName)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const message =
          (err as { body?: { message?: string } })?.body?.message ??
          'This invitation is not valid. Ask for a new one.'
        setLoadError(message)
      })

    return () => {
      cancelled = true
    }
  }, [token])

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setSubmitError(null)

    if (password !== confirm) {
      setSubmitError('The two passwords do not match.')
      return
    }

    setBusy(true)

    try {
      await api.post(`/api/Invites/${encodeURIComponent(token)}/accept`, {
        userName: userName.trim(),
        password,
      })

      // Sign in through the normal path rather than injecting the tokens the
      // accept call returned: one extra request, and the session is established
      // exactly the way every other session is.
      await login(userName.trim(), password)
      navigate('/desk', { replace: true })
    } catch (err: unknown) {
      setSubmitError(
        (err as { body?: { message?: string } })?.body?.message ??
          (err as { message?: string })?.message ??
          'Could not create the account.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="signin">
      <header className="signin__top">
        {/* A full load: "/" is the static homepage, not a route of this app. */}
        <a className="signin__brand" href="/" aria-label="OpenFNO homepage">
          <IconLogo className="signin__mark" />
          <span>OpenFNO</span>
        </a>
      </header>

      <main className="signin__main signin__main--one">
        <section className="signin__card" aria-labelledby="invite-title">
          <div className="signin__card-top">
            <span>An invitation</span>
            <span className="signin__paper">
              <i aria-hidden="true" /> Paper only
            </span>
          </div>
          <h1 id="invite-title" className="signin__title">Join the console</h1>

          {loadError ? (
            <p className="signin__error" role="alert">
              {loadError}
            </p>
          ) : !preview ? (
            <p className="signin__note">Checking the invitation…</p>
          ) : (
            <>
              <p className="signin__note">
                Invited as <b>{preview.email}</b>. Choose a username and a password — nobody else sees it, not even the admin
                who invited you.
              </p>

              {submitError && (
                <p className="signin__error" role="alert">
                  {submitError}
                </p>
              )}

              <form onSubmit={submit}>
                <div className="signin__field">
                  <label htmlFor="inv-user">Username</label>
                  <input id="inv-user" required value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="username" />
                </div>
                <div className="signin__field">
                  <label htmlFor="inv-pw">Password</label>
                  <input
                    id="inv-pw"
                    type="password"
                    minLength={8}
                    required
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="new-password"
                    aria-describedby="inv-pw-hint"
                  />
                  <small id="inv-pw-hint">At least 8 characters.</small>
                </div>
                <div className="signin__field">
                  <label htmlFor="inv-pw2">Confirm password</label>
                  <input
                    id="inv-pw2"
                    type="password"
                    minLength={8}
                    required
                    value={confirm}
                    onChange={(e) => setConfirm(e.target.value)}
                    autoComplete="new-password"
                  />
                </div>
                <button type="submit" className="signin__submit" disabled={busy}>
                  {busy ? 'Creating your account…' : 'Create my account'}
                </button>
              </form>

              <p className="signin__hint">
                Your account starts with no access. An admin decides what you can see and run — until then you can sign in,
                and that is all.
              </p>
            </>
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
