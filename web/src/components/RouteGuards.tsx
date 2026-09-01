/**
 * Route-level access control.
 *
 * These guards keep users out of screens they cannot use. They are a UX layer,
 * not a security boundary: the API enforces the same rules on every request, so
 * a user who edits their way past a guard still gets 403s.
 */

import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import type { UserRole } from '../lib/api'

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
  const { isAuthenticated, isAdmin, isLoading } = useAuth()

  if (isLoading) return <Splash label="Loading…" />

  if (isAuthenticated) {
    return <Navigate to={isAdmin ? '/admin' : '/trader'} replace />
  }

  return <Outlet />
}
