/**
 * The two pages the router falls back to: a route this user may not open,
 * and a route that does not exist.
 */

import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'

export function ForbiddenPage() {
  return (
    <div className="page page--centered">
      <h1 className="page__title">Not permitted</h1>
      <p className="page__subtitle">
        Your account does not have access to that area.
      </p>
      <Link className="btn btn--primary" to="/desk">
        Back to safety
      </Link>
    </div>
  )
}

export function NotFoundPage() {
  const { isAuthenticated } = useAuth()
  return (
    <div className="page page--centered">
      <h1 className="page__title">Page not found</h1>
      <p className="page__subtitle">That route does not exist.</p>
      <Link
        className="btn btn--primary"
        to={isAuthenticated ? '/desk' : '/'}
      >
        Go home
      </Link>
    </div>
  )
}
