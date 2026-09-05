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
      navigate('/trader', { replace: true })
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
    <div className="login">
      <div>
        <div className="login__card">
          <div className="login__brand">
            <span className="shell__brand-mark" aria-hidden="true">
              <IconLogo />
            </span>
            AlgoTrading Console
          </div>

          <h1 className="login__title">Join the console</h1>

        {loadError ? (
          <div className="alert alert--error" role="alert">
            {loadError}
          </div>
        ) : !preview ? (
          <p className="muted">Checking the invitation…</p>
        ) : (
          <>
            <p className="muted">
              Invited as <b>{preview.email}</b>. Choose a username and a password — nobody else sees
              it, not even the admin who invited you.
            </p>

            {submitError && (
              <div className="alert alert--error" role="alert">
                {submitError}
              </div>
            )}

            <form onSubmit={submit} className="login__form">
              <div className="field">
                <label className="field__label" htmlFor="inv-user">
                  Username
                </label>
                <input
                  id="inv-user"
                  className="field__input"
                  required
                  value={userName}
                  onChange={(e) => setUserName(e.target.value)}
                  autoComplete="username"
                />
              </div>
              <div className="field">
                <label className="field__label" htmlFor="inv-pw">
                  Password
                </label>
                <input
                  id="inv-pw"
                  className="field__input"
                  type="password"
                  minLength={8}
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="new-password"
                />
                <span className="small-note muted">At least 8 characters.</span>
              </div>
              <div className="field">
                <label className="field__label" htmlFor="inv-pw2">
                  Confirm password
                </label>
                <input
                  id="inv-pw2"
                  className="field__input"
                  type="password"
                  minLength={8}
                  required
                  value={confirm}
                  onChange={(e) => setConfirm(e.target.value)}
                  autoComplete="new-password"
                />
              </div>
              <button className="btn btn--primary" disabled={busy} style={{ width: '100%' }}>
                {busy ? 'Creating your account…' : 'Create my account'}
              </button>
            </form>

            <p className="small-note muted">
              Your account starts with no access. An admin decides what you can see and run — until
              then you can sign in, and that is all.
            </p>
          </>
        )}
        </div>
      </div>
    </div>
  )
}
