/**
 * Placeholder screens for the routes the shell already navigates to.
 *
 * These exist so the navigation, guards and layout can be exercised end to end
 * before any individual panel is built. Each is replaced by a real screen as
 * that feature lands.
 */

import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'

export function PagePlaceholder({
  title,
  description,
  endpoints,
}: {
  title: string
  description: string
  endpoints?: string[]
}) {
  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">{title}</h1>
        <p className="page__subtitle">{description}</p>
      </header>

      <div className="card card--dashed">
        <p className="card__muted">Not built yet.</p>
        {endpoints && endpoints.length > 0 && (
          <>
            <p className="card__muted">This screen will consume:</p>
            <ul className="endpoint-list">
              {endpoints.map((endpoint) => (
                <li key={endpoint}>
                  <code>{endpoint}</code>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  )
}

export function TraderHome() {
  const { user } = useAuth()
  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Welcome back, {user?.userName}</h1>
        <p className="page__subtitle">
          Allocated capital:{' '}
          {new Intl.NumberFormat('en-IN', {
            style: 'currency',
            currency: 'INR',
            maximumFractionDigits: 0,
          }).format(user?.totalCapital ?? 0)}
        </p>
      </header>

      <div className="card card--dashed">
        <p className="card__muted">
          Live P&amp;L, open positions and today&apos;s orders will appear here.
        </p>
      </div>
    </div>
  )
}

export function AdminHome() {
  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">System status</h1>
        <p className="page__subtitle">Platform health, ingestion and risk at a glance.</p>
      </header>

      <div className="card card--dashed">
        <p className="card__muted">
          Container health, ingestor heartbeat, kill-switch state and active strategies
          will appear here.
        </p>
      </div>
    </div>
  )
}

export function ForbiddenPage() {
  const { isAdmin } = useAuth()
  return (
    <div className="page page--centered">
      <h1 className="page__title">Not permitted</h1>
      <p className="page__subtitle">
        Your account does not have access to that area.
      </p>
      <Link className="btn btn--primary" to={isAdmin ? '/admin' : '/trader'}>
        Back to safety
      </Link>
    </div>
  )
}

export function NotFoundPage() {
  const { isAuthenticated, isAdmin } = useAuth()
  return (
    <div className="page page--centered">
      <h1 className="page__title">Page not found</h1>
      <p className="page__subtitle">That route does not exist.</p>
      <Link
        className="btn btn--primary"
        to={isAuthenticated ? (isAdmin ? '/admin' : '/trader') : '/'}
      >
        Go home
      </Link>
    </div>
  )
}
