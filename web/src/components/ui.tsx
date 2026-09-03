/**
 * Small shared UI pieces used across every screen. Kept deliberately plain —
 * the design system is the class vocabulary in styles.css, and these are just
 * ergonomic wrappers over it.
 */

import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { UseQueryResult } from '@tanstack/react-query'
import { formatPrice } from '../lib/format'

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
  tone?: 'pos' | 'neg' | 'warn' | 'neutral' | 'accent' | 'live'
  children: ReactNode
}) {
  return <span className={`badge badge--${tone}`}>{children}</span>
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>
}

/** Price text that flashes green/red when the value moves (live tables). */
export function FlashPrice({ value, bold }: { value: number | null | undefined; bold?: boolean }) {
  const prev = useRef<number | null>(null)
  const [flash, setFlash] = useState<{ dir: string; seq: number }>({ dir: '', seq: 0 })

  useEffect(() => {
    if (value != null && prev.current != null && value !== prev.current) {
      setFlash((f) => ({ dir: value > prev.current! ? 'flash-up' : 'flash-down', seq: f.seq + 1 }))
      const t = setTimeout(() => setFlash((f) => ({ ...f, dir: '' })), 900)
      return () => clearTimeout(t)
    }
    prev.current = value ?? prev.current
  }, [value])

  useEffect(() => {
    prev.current = value ?? null
  })

  return (
    <span
      key={flash.seq}
      className={`mono ${flash.dir}`}
      style={bold ? { fontWeight: 700 } : undefined}
    >
      {formatPrice(value)}
    </span>
  )
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
 *
 * A failed background refetch keeps showing the last good data (with a small
 * stale hint) instead of blanking a live table — one dropped poll during
 * market hours must not wipe a quotes panel.
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
  const data = query.data
  if (query.isError && data === undefined) return <InlineError error={query.error} />
  if (
    empty !== undefined &&
    (data == null || (Array.isArray(data) && data.length === 0))
  ) {
    return <EmptyState>{empty}</EmptyState>
  }
  return (
    <>
      {query.isError && (
        <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
          Refresh failed — showing the last loaded data.
        </p>
      )}
      {children(data as T)}
    </>
  )
}
