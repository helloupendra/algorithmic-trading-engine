/**
 * What today is likely to be, and how that went: GIFT Nifty and the
 * overnight markets, today's forecasts (range, trend day, up close) against
 * their baselines, how they scored after the close, FII and DII flows, and
 * the days ahead (events, holidays, expiries, results).
 *
 * The forecasts are labelled for what they are until the scoreboard says
 * otherwise: a model collecting evidence, read as context, not as a signal.
 */

import type { ForecastRow } from '../../lib/desk'
import {
  GIFT_KEY,
  NIFTY50,
  forecastRows,
  istHm,
  latestSnapshots,
  modelStanding,
  overnightRows,
  dayLabel,
  plainNumber,
  shiftDay,
  signedNumber,
  weekRows,
  weekdayOf,
} from '../../lib/desk'
import { PROOF, statusMeta } from '../../lib/analysis'
import { formatCrore } from '../../lib/factors'
import {
  useForecastScoreboard,
  useIntelCalendar,
  useIntelGlobalDaily,
  useIntelSnapshots,
  useMarketEvents,
  useMarketFlows,
} from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass, useDayForecasts, useDeskLegs } from './data'
import { Chip, Failed, PanelHead, RangeMeter, Waiting } from './parts'

// ---------------------------------------------------------------- overnight (market data)

export function Overnight({ view }: { view: DeskView }) {
  const snaps = useIntelSnapshots(view.today, true)
  const daily = useIntelGlobalDaily(shiftDay(view.today, -10), true)
  const gift = latestSnapshots(snaps.data).get(GIFT_KEY)
  const rows = overnightRows(snaps.data, daily.data)
  const last = snaps.data?.map((s) => s.fetchedUtc).sort().at(-1)
  return (
    <>
      <PanelHead title="Overnight" meta={`GIFT & global${last ? ` · ${istHm(last)}` : ''}`} />
      {snaps.isError && !snaps.data && <Failed what="The morning snapshots" error={snaps.error} />}
      {!snaps.data && !snaps.isError ? (
        <Waiting>Reading the morning snapshots…</Waiting>
      ) : (
        <>
          <div className="dk-gift">
            <div>
              <div className="dk-t3 dk-xs">GIFT Nifty</div>
              <div className="dk-gift__v dk-n">{gift ? plainNumber(gift.price, 1) : <span className="dk-t3">no snapshot yet</span>}</div>
            </div>
            {gift?.changePct != null && (
              <div style={{ textAlign: 'right' }}>
                <div className="dk-t3 dk-xs">vs its last close</div>
                <div className={`dk-n ${toneClass(gift.changePct)}`}>{signedNumber(gift.changePct)}%</div>
              </div>
            )}
          </div>
          {rows.length === 0 ? (
            <Waiting>No overseas prices recorded yet.</Waiting>
          ) : (
            <table className="dk-t">
              <tbody>
                {rows.map((r) => (
                  <tr key={r.key}>
                    <td className="dk-t2">{r.name}</td>
                    <td className="r dk-n">{plainNumber(r.value, r.digits)}</td>
                    <td className={`r dk-n ${toneClass(r.change)}`}>
                      {r.change == null ? '—' : r.isYield ? `${signedNumber(r.change, 0)} bp` : `${signedNumber(r.change)}%`}
                    </td>
                    <td className="r dk-t3 dk-n">{r.asOf}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </>
  )
}

// ---------------------------------------------------------------- forecasts (analysis)

const pct = (p: number) => `${Math.round(p * 100)}%`

/** "collecting · 12 of 60 live", from the scoreboard; "not proven" until it answers. */
function Standing() {
  const board = useForecastScoreboard()
  const s = modelStanding(board.data)
  if (!s) return <Chip>not proven</Chip>
  const meta = statusMeta(s.status)
  return (
    <Chip title={meta.meaning}>
      {meta.label.toLowerCase()} · {s.liveCount} of {PROOF.provenAt} live
    </Chip>
  )
}

export function Forecast({ view, links }: { view: DeskView; links: DeskLinks }) {
  const list = useDayForecasts(view)
  const rows = forecastRows(list.data, view.day)
  const issued = rows.map((r) => r.issuedUtc).filter(Boolean).sort()[0] ?? null
  return (
    <>
      <PanelHead
        title="Today’s forecast"
        meta={issued ? `issued ${istHm(issued)} · range.har-vix` : 'range.har-vix'}
        more={links.forecasts ? { to: links.forecasts, label: 'Analysis' } : null}
      />
      {list.isError && !list.data ? (
        <Failed what="Forecasts" error={list.error} />
      ) : !list.data ? (
        <Waiting>Reading today’s forecasts…</Waiting>
      ) : rows.length === 0 ? (
        <Waiting>None issued for {view.day === view.today ? 'today' : dayLabel(view.day)} yet; they come at 08:50 on trading days.</Waiting>
      ) : (
        <table className="dk-t">
          <thead>
            <tr>
              <th>Index</th>
              <th className="r">Range</th>
              <th className="r dk-hide-n">80%</th>
              <th className="r">Base</th>
              <th className="r">Trend day</th>
              <th className="r">Up close</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.underlying}>
                <td>{r.underlying}</td>
                <td className="r dk-n">{r.range ? Math.round(r.range.median) : '—'}</td>
                <td className="r dk-n dk-t3 dk-hide-n">{r.range ? `${Math.round(r.range.low80)}–${Math.round(r.range.high80)}` : '—'}</td>
                <td className="r dk-n dk-t3">{r.range?.baseMedian != null ? Math.round(r.range.baseMedian) : '—'}</td>
                <td className="r dk-n">
                  {r.trend ? pct(r.trend.p) : '—'}
                  {r.trend?.base != null && <span className="dk-t3"> / {Math.round(r.trend.base * 100)}</span>}
                </td>
                <td className="r dk-n">
                  {r.up ? pct(r.up.p) : '—'}
                  {r.up?.base != null && <span className="dk-t3"> / {Math.round(r.up.base * 100)}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="dk-foot">
        <Standing />
        <span>Points; model / baseline. Collecting, not proven: read as context, not as a signal.</span>
      </div>
    </>
  )
}

function Band({ row }: { row: ForecastRow }) {
  if (!row.scored || row.scored.inside == null) return <span className="dk-t3">—</span>
  return row.scored.inside ? <span className="pos">inside</span> : <span className="neg">outside</span>
}

export function Scores({ view, links }: { view: DeskView; links: DeskLinks }) {
  const list = useDayForecasts(view)
  const rows = forecastRows(list.data, view.day)
  const scored = rows.map((r) => r.scored?.scoredUtc).filter(Boolean).sort().at(-1) ?? null
  const issued = rows.map((r) => r.issuedUtc).filter(Boolean).sort()[0] ?? null
  return (
    <>
      <PanelHead
        title="Forecasts scored"
        meta={[issued ? `issued ${istHm(issued)}` : '', scored ? `scored ${istHm(scored)}` : 'not scored yet'].filter(Boolean).join(' · ')}
        more={links.forecasts ? { to: links.forecasts, label: 'Scoreboard' } : null}
      />
      {list.isError && !list.data ? (
        <Failed what="Forecasts" error={list.error} />
      ) : !list.data ? (
        <Waiting>Reading the forecasts…</Waiting>
      ) : rows.length === 0 ? (
        <Waiting>No forecasts were issued for this session.</Waiting>
      ) : (
        <table className="dk-t">
          <thead>
            <tr>
              <th>Index</th>
              <th className="r">Range fc · 80%</th>
              <th className="r">Actual</th>
              <th className="dk-hide-n" />
              <th>Band</th>
              <th className="r">P(up) · close</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.underlying}>
                <td>{r.underlying}</td>
                <td className="r dk-n dk-t2">
                  {r.range ? Math.round(r.range.median) : '—'}
                  {r.range && <span className="dk-t3 dk-xs"> {Math.round(r.range.low80)}–{Math.round(r.range.high80)}</span>}
                </td>
                <td className="r dk-n">{r.scored ? plainNumber(r.scored.range, 1) : <span className="dk-t3">after 15:50</span>}</td>
                <td className="dk-hide-n">{r.range && r.scored && <RangeMeter soFar={r.scored.range} median={r.range.median} low80={r.range.low80} high80={r.range.high80} width={72} />}</td>
                <td>
                  <Band row={r} />
                </td>
                <td className="r dk-n">
                  {r.up ? <span className="dk-t2">{pct(r.up.p)}</span> : '—'}{' '}
                  {r.scored && <span className={r.scored.up ? 'pos' : 'neg'}>{r.scored.up ? '↑' : '↓'}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="dk-foot">
        <Standing />
        <span>One session is not evidence. Proven needs {PROOF.provenAt} live sessions beating the baseline.</span>
      </div>
    </>
  )
}

// ---------------------------------------------------------------- flows (market data)

export function Flows({ view, links }: { view: DeskView; links: DeskLinks }) {
  const flows = useMarketFlows(20)
  const days = [...(flows.data?.cash ?? [])].sort((a, b) => a.date.localeCompare(b.date)).slice(-5)
  const last = days.at(-1)
  const fii = flows.data?.participants
    .slice()
    .sort((a, b) => a.date.localeCompare(b.date))
    .at(-1)
    ?.groups.find((g) => g.clientType.toUpperCase() === 'FII')
  const max = Math.max(1, ...days.flatMap((d) => [Math.abs(d.fii?.net ?? 0), Math.abs(d.dii?.net ?? 0)]))
  const today = view.clock === 'post' && last && last.date < view.today
  return (
    <>
      <PanelHead title="FII & DII" meta={last ? `${dayLabel(last.date)} · NSE provisional` : 'NSE provisional'} more={links.factors ? { to: links.factors, label: 'Flows' } : null} />
      {flows.isError && !flows.data ? (
        <Failed what="Flows" error={flows.error} />
      ) : !flows.data ? (
        <Waiting>Reading the flows…</Waiting>
      ) : !last ? (
        <Waiting>No flows recorded yet.</Waiting>
      ) : (
        <>
          <div className="dk-kv">
            <div>
              <div className="dk-xs dk-t3">FII cash</div>
              <div className={`dk-n ${toneClass(last.fii?.net)}`}>{formatCrore(last.fii?.net)}</div>
            </div>
            <div>
              <div className="dk-xs dk-t3">DII cash</div>
              <div className={`dk-n ${toneClass(last.dii?.net)}`}>{formatCrore(last.dii?.net)}</div>
            </div>
            <div>
              <div className="dk-xs dk-t3">FII index fut. long</div>
              <div className="dk-n">{fii?.futureIndexLongPercent != null ? `${fii.futureIndexLongPercent.toFixed(1)}%` : '—'}</div>
            </div>
          </div>
          <svg width={days.length * 34 + 4} height="58" className="dk-flows" role="img" aria-label="FII and DII net cash, last sessions">
            {days.map((d, i) => {
              const f = d.fii?.net ?? 0
              const di = d.dii?.net ?? 0
              const hF = (Math.abs(f) / max) * 22
              const hD = (Math.abs(di) / max) * 22
              return (
                <g key={d.date} transform={`translate(${i * 34 + 4},0)`}>
                  <rect x="0" y={f >= 0 ? 24 - hF : 24} width="7" height={hF} rx="1" fill={f >= 0 ? 'var(--pos)' : 'var(--neg)'} opacity=".85" />
                  <rect x="9" y={di >= 0 ? 24 - hD : 24} width="7" height={hD} rx="1" fill="var(--text-3)" opacity=".7" />
                  <text x="8" y="56" textAnchor="middle">
                    {weekdayOf(d.date)}
                  </text>
                </g>
              )
            })}
            <line x1="0" x2={days.length * 34 + 4} y1="24" y2="24" stroke="var(--line-strong)" />
          </svg>
          <div className="dk-xs dk-t3">
            <i className="dk-sw" style={{ background: 'var(--neg)' }} />
            FII <i className="dk-sw" style={{ background: 'var(--text-3)', marginLeft: 8 }} />
            DII · ₹ cr, last {days.length} sessions{today ? ' · today’s figures are due in the evening' : ''}
          </div>
        </>
      )}
    </>
  )
}

// ---------------------------------------------------------------- the week ahead (market data)

const KIND_TONE = { expiry: 'brand', holiday: 'warn', event: undefined, results: undefined, meeting: undefined } as const

/** Board meetings of the desk's names: NIFTY 50 and whatever this viewer holds. */
function WithMeetings({ view, links, from, to }: { view: DeskView; links: DeskLinks; from: string; to: string }) {
  const meetings = useIntelCalendar(from, to, true)
  const { legs } = useDeskLegs(view)
  const names = new Set([...NIFTY50, ...(legs ?? []).map((l) => l.underlying)])
  return <WeekTable view={view} links={links} from={from} to={to} meetings={meetings.data} names={names} />
}

function WeekTable({
  view,
  links,
  from,
  to,
  meetings,
  names,
}: {
  view: DeskView
  links: DeskLinks
  from: string
  to: string
  meetings: Parameters<typeof weekRows>[1]
  names: ReadonlySet<string>
}) {
  const events = useMarketEvents(from, to)
  const rows = weekRows(events.data?.events, meetings, names, from, to)
  return (
    <>
      <PanelHead
        title={view.phase === 'post' ? 'Tomorrow & this week' : 'This week'}
        meta="events, expiries, results"
        more={links.factors ? { to: `${links.factors}?section=events`, label: 'Calendar' } : null}
      />
      {events.isError && !events.data ? (
        <Failed what="The calendar" error={events.error} />
      ) : !events.data ? (
        <Waiting>Reading the calendar…</Waiting>
      ) : rows.length === 0 ? (
        <Waiting>Nothing scheduled in the next few days.</Waiting>
      ) : (
        <table className="dk-t dk-t--wrap">
          <tbody>
            {rows.slice(0, 9).map((r) => (
              <tr key={r.key}>
                <td className="dk-t2 dk-n" style={{ width: 78, whiteSpace: 'nowrap' }}>
                  {dayLabel(r.date)}
                </td>
                <td className="dk-t3 dk-n dk-hide-n" style={{ width: 44 }}>
                  {r.time ?? '—'}
                </td>
                <td>{r.title}</td>
                <td className="r" style={{ width: 1 }}>
                  <Chip tone={KIND_TONE[r.kind]}>{r.kind}</Chip>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

export function Week({ view, links }: { view: DeskView; links: DeskLinks }) {
  // After the close the day is done: the list starts tomorrow.
  const from = view.phase === 'post' ? shiftDay(view.day, 1) : view.day
  const to = shiftDay(from, 7)
  return <WithMeetings view={view} links={links} from={from} to={to} />
}
