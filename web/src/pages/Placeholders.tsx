/**
 * The two pages the router falls back to: a route this user may not open,
 * and a route that does not exist. Inside the console the strip names the
 * page (AppLayout takes the name from lib/modules.ts), so the page itself is
 * the one line that says what to do next; on its own, for a visitor who is
 * not signed in, it carries the heading too.
 */

import type { ReactNode } from 'react'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { FORBIDDEN_PAGE, NOT_FOUND_LABEL } from '../lib/modules'
import { useInShell } from '../components/shell/stripSlot'

/* One card for both: the title, one sentence and the one way out, in the
   console's panel language, so a wrong turn reads like any other page. */
function Fallback({ title, children, action }: { title: string; children: ReactNode; action: ReactNode }) {
  const inShell = useInShell()
  return (
    <div className={inShell ? 'page' : 'page page--centered'}>
      <section className="panel fallback" aria-labelledby="fallback-title">
        <h1 className="fallback__title" id="fallback-title">
          {title}
        </h1>
        <p className="fallback__text">{children}</p>
        <div className="fallback__actions">{action}</div>
      </section>
    </div>
  )
}

export function ForbiddenPage() {
  return (
    <Fallback
      title={FORBIDDEN_PAGE.label}
      action={
        <Link className="btn btn--primary" to="/desk">
          Back to the Desk
        </Link>
      }
    >
      Your account does not have access to that area. An admin grants modules on System → People; until then the Desk
      and your Account are yours.
    </Fallback>
  )
}

export function NotFoundPage() {
  const { isAuthenticated } = useAuth()
  const { pathname } = useLocation()
  return (
    <Fallback
      title={NOT_FOUND_LABEL}
      action={
        <Link className="btn btn--primary" to={isAuthenticated ? '/desk' : '/'}>
          {isAuthenticated ? 'Open the Desk' : 'Open the homepage'}
        </Link>
      }
    >
      There is no page at <code>{pathname}</code>. The address may have moved; the sidebar lists every page you can
      open.
    </Fallback>
  )
}

/**
 * The router's last route. A signed-in user keeps the console around the
 * page that is not there (the route under this mounts AppLayout, then
 * NotFoundPage); a visitor gets the page on its own. Nothing here redirects,
 * so a mistyped URL never bounces a visitor to sign-in.
 */
export function FallbackShell() {
  const { isAuthenticated, isLoading } = useAuth()
  if (isLoading) return null
  return isAuthenticated ? <Outlet /> : <NotFoundPage />
}
