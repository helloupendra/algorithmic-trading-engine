/**
 * The two pages the router falls back to: a route this user may not open,
 * and a route that does not exist. Inside the console the strip names the
 * page (AppLayout takes the name from lib/modules.ts), so the page itself is
 * the one line that says what to do next; on its own, for a visitor who is
 * not signed in, it carries the heading too.
 */

import { Link, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { FORBIDDEN_PAGE, NOT_FOUND_LABEL } from '../lib/modules'
import { useInShell } from '../components/shell/stripSlot'

export function ForbiddenPage() {
  const inShell = useInShell()
  return (
    <div className={inShell ? 'page' : 'page page--centered'}>
      {!inShell && <h1 className="page__title">{FORBIDDEN_PAGE.label}</h1>}
      <p className="page__subtitle">Your account does not have access to that area.</p>
      <p>
        <Link className="btn" to="/desk">
          Back to the Desk
        </Link>
      </p>
    </div>
  )
}

export function NotFoundPage() {
  const { isAuthenticated } = useAuth()
  const { pathname } = useLocation()
  const inShell = useInShell()
  return (
    <div className={inShell ? 'page' : 'page page--centered'}>
      {!inShell && <h1 className="page__title">{NOT_FOUND_LABEL}</h1>}
      <p className="page__subtitle">
        There is no page at <code>{pathname}</code>.
      </p>
      <p>
        <Link className="btn btn--primary" to={isAuthenticated ? '/desk' : '/'}>
          {isAuthenticated ? 'Open the Desk' : 'Open the homepage'}
        </Link>
      </p>
    </div>
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
