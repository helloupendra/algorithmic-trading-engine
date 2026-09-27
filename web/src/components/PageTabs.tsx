/**
 * A page's own tabs when each tab is a URL of its own (/system/checkups,
 * /markets/chain/oi): the console's one tab style (.oc-tabs), as links, so a
 * tab can be bookmarked, opened in a new window and reached with Back. A tab
 * whose `to` carries a query keeps it; the current tab is marked for a
 * screen reader as the current page.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'

export interface PageTab {
  key: string
  label: ReactNode
  to: string
}

export function PageTabs({
  label,
  tabs,
  current,
  className,
}: {
  /** What the tabs choose between, for a screen reader. */
  label: string
  tabs: readonly PageTab[]
  current: string
  className?: string
}) {
  return (
    <nav className={`oc-tabs ${className ?? ''}`} aria-label={label}>
      {tabs.map((t) => (
        <Link
          key={t.key}
          to={t.to}
          className={`oc-tab ${t.key === current ? 'oc-tab--on' : ''}`}
          aria-current={t.key === current ? 'page' : undefined}
        >
          {t.label}
        </Link>
      ))}
    </nav>
  )
}
