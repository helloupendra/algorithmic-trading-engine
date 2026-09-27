/**
 * Open legs: every leg open right now, strategy runs and manual books alike,
 * the largest open P&L first. Before the open it is what was carried in;
 * after the close, what is held overnight (carried legs and MCX runs), with
 * the ones expiring next flagged.
 *
 * One read for every underlying and every book (GET /api/Positions/open):
 * the API marks each leg at the live price and scopes the list (a trader
 * gets their own; an admin, every account's carried legs as well as their
 * own). The P&L is the open move before exit charges; the charges of the
 * fills so far are already in the run's net on the grid.
 */

import { dayMonth, plainNumber, weekdayOf } from '../../lib/desk'
import { formatInrSigned } from '../../lib/format'
import type { DeskLinks, DeskView } from './data'
import { useDeskLegs } from './data'
import { Chip, Failed, Money, PanelHead, Swatch, Waiting } from './parts'

const SHOWN = 8

export function Legs({ view, links }: { view: DeskView; links: DeskLinks }) {
  const { legs, error } = useDeskLegs(view)
  const title = view.phase === 'pre' ? 'Carried in' : view.phase === 'post' ? 'Held overnight' : 'Open legs'
  const list = legs ? (view.phase === 'pre' ? legs.filter((l) => l.carriedFrom) : legs) : null
  const meta =
    list == null
      ? undefined
      : view.phase === 'live'
        ? `${list.length} open${list.length > SHOWN ? ` · largest ${SHOWN}` : ''} · before exit charges`
        : view.phase === 'post'
          ? 'carried legs and MCX runs'
          : 'held from the last session'
  const toneOf = new Map(view.allAccounts.map((a) => [a.id, a.tone]))
  const multi = view.accounts.length > 1

  return (
    <>
      <PanelHead title={title} meta={meta} more={links.positions ? { to: links.positions, label: 'Positions' } : null} />
      {list == null ? (
        error ? <Failed what="The open legs" error={error} /> : <Waiting>Reading the open legs…</Waiting>
      ) : list.length === 0 ? (
        <Waiting>{view.phase === 'pre' ? 'Nothing was carried in.' : view.phase === 'post' ? 'Nothing is held overnight.' : 'No leg is open.'}</Waiting>
      ) : (
        <>
          <table className="dk-t">
            <thead>
              <tr>
                <th>Leg</th>
                <th className="r">Lots</th>
                <th className="r dk-hide-s">LTP</th>
                <th className="r">P&L</th>
              </tr>
            </thead>
            <tbody>
              {list.slice(0, SHOWN).map((l) => (
                <tr key={l.key} title={`${l.userName} · ${l.manual ? 'manual book' : l.strategy} · ${l.short ? 'short' : 'long'}${l.carryForward ? ' · ticked to carry' : ''} · opened ${new Date(l.openedUtc).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })}${l.pnl != null ? ` · ${formatInrSigned(l.pnl)} open` : ''}`}>
                  <td className="dk-leg">
                    {multi && <Swatch tone={toneOf.get(l.userId) ?? null} />}
                    {l.label}
                    {l.expiryDate && <span className="dk-t3 dk-xs"> {dayMonth(l.expiryDate)}</span>}
                    {l.short && <span className="dk-t3 dk-xs"> short</span>}
                    {l.carriedFrom && (
                      <>
                        {' '}
                        <Chip tone="warn">from {l.carriedFrom}</Chip>
                      </>
                    )}
                    {l.manual && !l.carriedFrom && (
                      <>
                        {' '}
                        <Chip title="Held in the manual book">book</Chip>
                      </>
                    )}
                    {l.carryForward && !l.carriedFrom && (
                      <>
                        {' '}
                        <Chip title={l.manual ? 'Ticked to be held overnight' : 'Ticked to move to the manual book at the close'}>carry</Chip>
                      </>
                    )}
                    {/* An expiry matters on the day, and overnight for what is held into it. */}
                    {(l.expires === 'today' || (l.expires === 'next' && view.phase === 'post')) && l.expiryDate && (
                      <>
                        {' '}
                        <Chip tone="warn" title={`Expires ${dayMonth(l.expiryDate)}`}>
                          exp. {l.expires === 'today' ? 'today' : weekdayOf(l.expiryDate)}
                        </Chip>
                      </>
                    )}
                  </td>
                  <td className="r dk-n dk-t2">{l.lots}L</td>
                  <td className="r dk-n dk-hide-s">{l.ltp != null ? plainNumber(l.ltp, l.ltp >= 1000 ? 0 : 2) : '—'}</td>
                  <td className="r">{l.pnl != null ? <Money value={l.pnl} compact /> : <span className="dk-t3">no mark</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.length > SHOWN && <div className="dk-foot">{list.length - SHOWN} smaller legs not shown</div>}
        </>
      )}
    </>
  )
}
