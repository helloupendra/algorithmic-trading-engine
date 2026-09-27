/**
 * Route-level access control, and the two small routes that choose where to go.
 *
 * These guards keep users out of screens they cannot use. They are a UX layer,
 * not a security boundary: the API enforces the same rules on every request, so
 * a user who edits their way past a guard still gets 403s.
 */

import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import type { UserRole } from '../lib/api'
import { accessFor, navFor } from '../lib/modules'
import { movedUrl } from '../lib/routeMap'

/**
 * An old URL, sent on to where it lives now with its query string and hash:
 * a Telegram message or a bookmark from before the workspaces still opens the
 * right page. Mounted outside the sign-in guard, so the sign-in page returns
 * to the new URL rather than to one that only redirects.
 */
export function MovedTo() {
  const { pathname, search, hash } = useLocation()
  const to = movedUrl(pathname, search)
  return <Navigate to={to ? `${to}${hash}` : '/desk'} replace />
}

/**
 * A workspace's own address when no page lives at it (/trade, /research):
 * the tab a click on the workspace opens for this user.
 */
export function WorkspaceLanding() {
  const { user } = useAuth()
  const { pathname } = useLocation()
  const workspace = navFor(accessFor(user)).find((w) => w.key === pathname.split('/')[1])
  return <Navigate to={workspace?.to ?? '/desk'} replace />
}

/**
 * One URL, a view per role. The API already scopes what it answers to the
 * caller; this only picks which page renders it.
 */
export function ByRole({ admin, trader }: { admin: ReactNode; trader: ReactNode }) {
  const { isAdmin } = useAuth()
  return <>{isAdmin ? admin : trader}</>
}

function Splash({ label }: { label: string }) {
  return (
    <div className="splash" role="status" aria-live="polite">
      <div className="splash__spinner" aria-hidden="true" />
      <p>{label}</p>
    </div>
  )
}

/** Requires any signed-in user. Remembers where they were headed. */
export function RequireAuth() {
  const { isAuthenticated, isLoading } = useAuth()
  const location = useLocation()

  if (isLoading) return <Splash label="Restoring your session…" />

  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location }} />
  }

  return <Outlet />
}

/** Requires a specific role on top of being signed in. */
export function RequireRole({ role }: { role: UserRole }) {
  const { user, isLoading } = useAuth()

  if (isLoading) return <Splash label="Checking permissions…" />

  if (user?.role !== role) {
    return <Navigate to="/forbidden" replace />
  }

  return <Outlet />
}

/** Keeps a signed-in user off the login page. */
export function RedirectIfAuthenticated() {
  const { isAuthenticated, isLoading } = useAuth()

  if (isLoading) return <Splash label="Loading…" />

  if (isAuthenticated) {
    return <Navigate to="/desk" replace />
  }

  return <Outlet />
}
