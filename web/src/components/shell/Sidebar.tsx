/**
 * The sidebar (sprout v1, 8 Nov 2026): the brand, the workspaces in three
 * groups, the current workspace opened to its pages, and the market session
 * at the foot. Everything in it comes from lib/modules.ts and the URL, so it
 * has no navigation state of its own; it is hidden on a phone, where the
 * bottom bar and the tab strip take over.
 */

import { Link } from 'react-router-dom'
import type { NavPage, NavWorkspace, WorkspaceKey } from '../../lib/modules'
import { IconLogo } from '../icons'
import { SessionCard } from './SessionCard'

/** Today and the Desk stand alone; then the trading workspaces, then the rest. */
const GROUPS: { label: string | null; keys: readonly WorkspaceKey[] }[] = [
  { label: null, keys: ['today', 'desk'] },
  { label: 'Trading', keys: ['markets', 'trade', 'portfolio'] },
  { label: 'Workspace', keys: ['research', 'ai', 'data', 'system'] },
]

export function Sidebar({ nav, current, page }: { nav: NavWorkspace[]; current: WorkspaceKey | undefined; page: NavPage | undefined }) {
  const home = nav[0]
  return (
    <aside className="shell__side" aria-label="Console">
      <Link to={home?.to ?? '/'} className="shell__brand" aria-label={`OpenFNO: ${home?.label ?? 'home'}`}>
        <IconLogo className="shell__brand-mark" />
        <span className="shell__brand-word">OpenFNO</span>
      </Link>
      <nav className="side" aria-label="Workspaces">
        {GROUPS.map((group) => {
          const items = nav.filter((ws) => group.keys.includes(ws.key))
          if (items.length === 0) return null
          return (
            <div className="side__group" key={group.keys[0]}>
              {group.label && <p className="side__label">{group.label}</p>}
              {items.map((ws) => {
                const Icon = ws.icon
                const on = ws.key === current
                return (
                  <div key={ws.key} className={`side__ws${on ? ' is-on' : ''}`}>
                    <Link to={ws.to} className="side__link" aria-current={on && ws.pages.length <= 1 ? 'page' : on ? 'true' : undefined}>
                      <Icon aria-hidden="true" />
                      <span>{ws.label}</span>
                    </Link>
                    {on && ws.pages.length > 1 && (
                      <ul className="side__pages">
                        {ws.pages.map((p) => (
                          <li key={p.to}>
                            <Link to={p.to} className="side__page" aria-current={p === page ? 'page' : undefined}>
                              {p.label}
                            </Link>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                )
              })}
            </div>
          )
        })}
      </nav>
      <SessionCard />
    </aside>
  )
}
