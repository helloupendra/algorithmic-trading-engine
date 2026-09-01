/**
 * Small shared UI pieces used across every screen. Kept deliberately plain —
 * the design system is the class vocabulary in styles.css, and these are just
 * ergonomic wrappers over it.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { UseQueryResult } from '@tanstack/react-query'

export function Panel({
  title,
  actions,
  children,
  className,
}: {
  title?: ReactNode
  actions?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={`panel ${className ?? ''}`}>
      {(title || actions) && (
        <header className="panel__head">
          {title && <h2 className="panel__title">{title}</h2>}
          {actions && <div className="panel__actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  )
}

export function StatTile({
  label,
  value,
  sub,
  tone,
  to,
}: {
  label: string
  value: ReactNode
  sub?: ReactNode
  tone?: 'pos' | 'neg' | 'warn' | 'accent'
  /** When set, the whole tile is a link to this route. */
  to?: string
}) {
  const body = (
    <>
      <div className={`stat__value ${tone ?? ''}`}>{value}</div>
      <div className="stat__label">{label}</div>
      {sub && <div className="stat__sub">{sub}</div>}
    </>
  )
  if (to) {
    return (
      <Link className="stat stat--link" to={to}>
        {body}
      </Link>
    )
  }
  return <div className="stat">{body}</div>
}

export function Badge({
  tone = 'neutral',
  children,
}: {
  tone?: 'pos' | 'neg' | 'warn' | 'neutral' | 'accent'
  children: ReactNode
}) {
  return <span className={`badge badge--${tone}`}>{children}</span>
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>
}

export function InlineError({ error }: { error: unknown }) {
  // API failures in development carry a full stack trace; the first line is
  // the human-readable part, the rest is noise for an operator screen.
  const raw = error instanceof Error ? error.message : 'Something went wrong.'
  const firstLine = raw.split('\n')[0].split(' at ')[0].trim()
  const message = firstLine.length > 240 ? `${firstLine.slice(0, 240)}…` : firstLine
  return (
    <div className="alert alert--error" role="alert">
      {message || 'Something went wrong.'}
    </div>
  )
}

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <p className="empty" role="status">
      {label}
    </p>
  )
}

/**
 * Renders the three states of a query without each page re-writing them.
 * Data is only handed to children when it exists.
 */
export function QueryBoundary<T>({
  query,
  empty,
  children,
}: {
  query: UseQueryResult<T>
  empty?: ReactNode
  children: (data: T) => ReactNode
}) {
  if (query.isPending) return <Loading />
  if (query.isError) return <InlineError error={query.error} />
  const data = query.data
  if (
    empty !== undefined &&
    (data == null || (Array.isArray(data) && data.length === 0))
  ) {
    return <EmptyState>{empty}</EmptyState>
  }
  return <>{children(data)}</>
}
