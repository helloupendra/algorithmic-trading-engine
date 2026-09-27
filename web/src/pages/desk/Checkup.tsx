/**
 * Desk checkup (admin): Sentinel's latest checklist, with what to do on each
 * item that needs a person. Before the open it is the readiness list; after
 * the close, the after-close one. On a phone the passed checks fold into one
 * line, so the ones that need a hand are what is read.
 */

import { Link } from 'react-router-dom'
import { itemLink, slotLabel, stateBadge, staleNote, verdictBadge } from '../../lib/checkup'
import { istHm } from '../../lib/desk'
import { formatDateTime } from '../../lib/format'
import { useCheckupLatest } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { Chip, Dot, Failed, PanelHead, Waiting } from './parts'

const STATE_DOT: Record<string, 'pos' | 'warn' | 'neg' | null> = { ok: 'pos', warn: 'warn', fail: 'neg', info: null, skip: null }

export function Checkup({ view, links }: { view: DeskView; links: DeskLinks }) {
  const latest = useCheckupLatest()
  const c = latest.data?.latest
  const head = (
    <PanelHead
      title="Desk checkup"
      meta={c ? `${slotLabel(c.slot).toLowerCase()} · ${istHm(c.completedUtc)} · Sentinel` : 'Sentinel'}
      more={links.checkups ? { to: links.checkups, label: 'All checkups' } : null}
    />
  )
  if (latest.isError && !latest.data) return <>{head}<Failed what="The checkup" error={latest.error} /></>
  if (!latest.data) return <>{head}<Waiting>Reading the latest checkup…</Waiting></>
  if (!c) return <>{head}<Waiting>No checkup has finished yet.</Waiting></>
  const badge = verdictBadge(c)
  const stale = staleNote(latest.data.lastCompletedUtc, view.nowMs, formatDateTime)
  const passed = c.items.filter((i) => i.state === 'ok').length
  return (
    <>
      {head}
      <div className="dk-hd" style={{ marginTop: -2, alignItems: 'center' }}>
        <Chip tone={badge.tone === 'pos' ? 'pos' : badge.tone === 'neg' ? 'neg' : badge.tone === 'warn' ? 'warn' : undefined}>{badge.label}</Chip>
        <span className="dk-s dk-t2" style={{ textTransform: 'none', letterSpacing: 0 }}>
          {c.headline}
        </span>
      </div>
      {stale && <p className="dk-fail">{stale}</p>}
      <table className="dk-t dk-t--wrap">
        <tbody>
          {c.items.map((item) => {
            const link = itemLink(item.link)
            const state = stateBadge(item.state)
            return (
              <tr key={item.key} className={item.state === 'ok' ? 'dk-okrow' : undefined}>
                <td style={{ width: 14 }} title={state.label}>
                  <Dot tone={STATE_DOT[item.state] ?? null} />
                </td>
                <td className="dk-t2" style={{ width: 120 }}>
                  {link ? (
                    <Link to={link} style={{ color: 'inherit', textDecoration: 'none' }}>
                      {item.title}
                    </Link>
                  ) : (
                    item.title
                  )}
                </td>
                <td>
                  {item.detail}
                  {item.action && <div className="warn dk-s">{item.action}</div>}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {passed > 0 && <div className="dk-okmore dk-xs dk-t3">{passed} more checks passed</div>}
    </>
  )
}
