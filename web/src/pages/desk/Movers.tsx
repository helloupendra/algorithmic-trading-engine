/**
 * Breadth & F&O movers: NSE-wide advances and declines for the last session
 * (from NSE's evening files; admin, like the rest of market intelligence),
 * then Angel One's F&O gainers and losers with their open-interest build-up.
 * Angel caches its screens for 45 s and this asks no faster.
 */

import type { MoverRow } from '../../lib/movers'
import { BUILD_UPS } from '../../lib/factors'
import { buildUpTone } from '../../lib/movers'
import { dayLabel, istHm, shiftDay, signedNumber } from '../../lib/desk'
import { useAngelMovers, useIntelBreadth } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass } from './data'
import { Chip, Failed, PanelHead, Waiting } from './parts'

const SHORT: Record<string, string> = {
  'Long build-up': 'LB',
  'Short covering': 'SC',
  'Short build-up': 'SB',
  'Long unwinding': 'LU',
}

function Breadth({ view }: { view: DeskView }) {
  const breadth = useIntelBreadth(shiftDay(view.day, -10), true)
  const last = [...(breadth.data ?? [])].sort((a, b) => a.date.localeCompare(b.date)).at(-1)
  if (breadth.isError && !breadth.data) return <Failed what="Breadth" error={breadth.error} />
  if (!breadth.data) return <Waiting>Reading breadth…</Waiting>
  if (!last) return <Waiting>No breadth recorded yet.</Waiting>
  const total = Math.max(1, last.advances + last.declines + last.unchanged)
  return (
    <div className="dk-breadth" title="NSE-wide, from the session's files; written each evening">
      <div className="dk-breadth__l">
        <span className="dk-t2">NSE · {dayLabel(last.date)}</span>
        <span className="dk-n">
          <span className="pos">{last.advances.toLocaleString('en-IN')}▲</span> <span className="neg">{last.declines.toLocaleString('en-IN')}▼</span>
        </span>
      </div>
      <div className="dk-bar2" aria-hidden="true">
        <i style={{ width: `${(last.advances / total) * 100}%`, background: 'var(--pos)' }} />
        <i style={{ width: `${(last.unchanged / total) * 100}%`, background: 'var(--text-3)' }} />
        <i style={{ width: `${(last.declines / total) * 100}%`, background: 'var(--neg)' }} />
      </div>
      <div className="dk-breadth__l dk-xs dk-t3">
        <span>{last.unchanged.toLocaleString('en-IN')} unchanged</span>
        {last.highs52w != null && last.lows52w != null && (
          <span>
            52w highs {last.highs52w} · lows {last.lows52w}
          </span>
        )}
      </div>
    </div>
  )
}

function MoverTable({ rows }: { rows: MoverRow[] }) {
  return (
    <table className="dk-t">
      <tbody>
        {rows.map((r) => {
          const tone = buildUpTone(r.buildUp)
          return (
            <tr key={r.symbol}>
              <td>{r.underlying || r.symbol}</td>
              <td className={`r dk-n ${toneClass(r.priceChangePercent)}`}>{signedNumber(r.priceChangePercent)}%</td>
              <td className="r" style={{ width: 26 }}>
                {r.buildUp && SHORT[r.buildUp] ? (
                  <Chip tone={tone === 'neutral' ? undefined : tone} title={`${r.buildUp}${r.oiChangePercent != null ? ` · OI ${signedNumber(r.oiChangePercent, 1)}%` : ''}`}>
                    {SHORT[r.buildUp]}
                  </Chip>
                ) : null}
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

export function Movers({ view, links }: { view: DeskView; links: DeskLinks }) {
  const movers = useAngelMovers('NEAR')
  const m = movers.data
  const meta = m?.asOfUtc ? `Angel One · ${istHm(m.asOfUtc)}` : 'Angel One'
  return (
    <>
      <PanelHead title={view.isAdmin ? 'Breadth & F&O movers' : 'F&O movers'} meta={meta} more={links.movers ? { to: links.movers, label: 'Movers' } : null} />
      {view.isAdmin && <Breadth view={view} />}
      {movers.isError && !m ? (
        <Failed what="Movers" error={movers.error} />
      ) : !m ? (
        <Waiting>Reading the movers…</Waiting>
      ) : !m.configured ? (
        <Waiting>{view.isAdmin ? `Angel One is not set up${m.missing?.length ? `: ${m.missing.join(', ')} missing` : ''}.` : 'Movers are not available right now.'}</Waiting>
      ) : (
        <>
          <div className="dk-two">
            <MoverTable rows={(m.priceGainers ?? []).slice(0, 5)} />
            <MoverTable rows={(m.priceLosers ?? []).slice(0, 5)} />
          </div>
          <div className="dk-foot">{BUILD_UPS.map((b) => `${SHORT[b]} ${b.toLowerCase()}`).join(' · ')}</div>
        </>
      )}
    </>
  )
}
