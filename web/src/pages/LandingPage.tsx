/**
 * "/" inside the app. The homepage is static HTML that the API serves at "/"
 * (home.html; see ConsoleSite.UseConsoleDefaultFiles), so a route of this app
 * reaches "/" only by a client-side navigation (signing out, a link home) and
 * hands over to the real page with a full load. If this document was itself
 * served for "/" (a wwwroot built without home.html), loading "/" again would
 * only serve it again: it offers the way in instead of looping.
 */

import { useEffect } from 'react'

/** Read once, as the app boots: whether the server answered "/" with the app itself. */
const BOOTED_AT_ROOT = typeof window !== 'undefined' && window.location.pathname === '/'

export function LandingPage() {
  const stuck = BOOTED_AT_ROOT && !import.meta.env.DEV
  useEffect(() => {
    // The dev server serves the app at "/", and the homepage at its own name.
    if (!stuck) window.location.replace(import.meta.env.DEV ? '/home.html' : '/')
  }, [stuck])
  if (!stuck) return null
  return (
    <main className="page" style={{ maxWidth: 560, margin: '15vh auto', padding: 24 }}>
      <h1>OpenFNO</h1>
      <p>A self-hosted paper-trading desk for Indian F&amp;O.</p>
      <p>
        <a href="/login">Sign in</a> · <a href="/docs/">Read the docs</a> ·{' '}
        <a href="https://github.com/helloupendra/algorithmic-trading-engine">Source on GitHub</a>
      </p>
    </main>
  )
}
