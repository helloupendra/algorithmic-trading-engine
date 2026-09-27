import { useCallback, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import type { MeResponse } from '../../lib/api'
import { ACCOUNT_PAGE } from '../../lib/modules'
import { IconSignOut, IconUsers } from '../icons'
import { useDismiss } from './useDismiss'

/**
 * The avatar: who is signed in, the trader's own Account page (their broker
 * account and capital) and signing out. An admin has no Account page; the
 * people and grants live under System.
 */
export function UserMenu({ user, isAdmin, onSignOut }: { user: MeResponse | null; isAdmin: boolean; onSignOut: () => void }) {
  const [open, setOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)
  const close = useCallback(() => setOpen(false), [])
  useDismiss(boxRef, open, close)

  const name = user?.userName ?? ''
  const initials = (name || '?').slice(0, 2).toUpperCase()

  return (
    <div className="shell__user" ref={boxRef}>
      <button
        type="button"
        className="shell__avatar"
        aria-haspopup="true"
        aria-expanded={open}
        aria-controls="user-menu"
        aria-label={`Account: ${name}`}
        title={name}
        onClick={() => setOpen((v) => !v)}
      >
        {initials}
      </button>
      {open && (
        <div className="pop pop--menu" id="user-menu">
          <div className="pop__who">
            <b>{name}</b>
            <span data-role={user?.role}>{user?.role}</span>
          </div>
          {!isAdmin && (
            <Link to={ACCOUNT_PAGE.to} className="pop__item">
              <IconUsers aria-hidden="true" /> {ACCOUNT_PAGE.label}
            </Link>
          )}
          <button type="button" className="pop__item" onClick={onSignOut}>
            <IconSignOut aria-hidden="true" /> Sign out
          </button>
        </div>
      )}
    </div>
  )
}
