/**
 * Market factors: the numbers desks read to judge where the market may go,
 * gathered on one page.
 *
 * Three sections, one at a time (the choice lives in the URL):
 *  - FII & DII: how FIIs, DIIs, pros and clients sit in index futures and
 *    index options at the day's close, and FII futures and FII/DII cash
 *    buying and selling over the last sessions as charts (each with its
 *    table a click away), from NSE's evening files;
 *  - Global: GIFT Nifty and the overseas markets that set the tone before 09:15;
 *  - Events: RBI and Fed decisions, US inflation prints, expiries and holidays.
 *
 * Every section says how fresh its numbers are and what our own tests found for
 * that factor, so a reading is never mistaken for a proven signal.
 *
 * Two sections moved to the pages that read the same data: the chain's
 * levels are the Levels tab of Markets → Option chain, and the futures
 * build-up is a section of Markets → Movers. An old ?section= link to either
 * is sent there.
 */

import { useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import {
  useAddMarketEvent,
  useDeleteMarketEvent,
  useGlobalCues,
  useMarketEvents,
  useMarketFlows,
} from '../../lib/queries'
import {
  EVENT_CATEGORIES,
  cashStreak,
  cashTrend,
  formatCrore,
  formatDay,
  formatSignedContracts,
  futuresTrend,
  gapReading,
  grouped,
  istDate,
  movedFactorSection,
  optionsPositions,
  participantStance,
  signTone,
  splitEvents,
} from '../../lib/factors'
import type { CashDay, GlobalCue, MarketEvent, ParticipantDay } from '../../lib/factors'
import { formatOi, movePercent } from '../../lib/movers'
import { formatAge, formatDateTime } from '../../lib/format'
import { useAuth } from '../../lib/auth'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { Metric, ResearchNote, StatusLine } from '../markets/factorParts'
import { OptionsNetChart, SessionChart } from './FactorCharts'
import type { SessionPane } from './FactorCharts'
import './factors.css'

const SECTIONS = [
  { key: 'flows', label: 'FII & DII' },
  { key: 'global', label: 'Global' },
  { key: 'events', label: 'Events' },
] as const
type SectionKey = (typeof SECTIONS)[number]['key']

function sectionFrom(value: string | null): SectionKey {
  return (SECTIONS.find((s) => s.key === value)?.key ?? 'flows') as SectionKey
}

// ---------------------------------------------------------------------------
// FII & DII
// ---------------------------------------------------------------------------

/** Chart or table: the same numbers, one click apart. */
function ViewSwitch<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T
  options: ReadonlyArray<{ key: T; label: string }>
  onChange: (next: T) => void
  label: string
}) {
  return (
    <div className="oc-seg" role="group" aria-label={label}>
      {options.map((o) => (
        <button key={o.key} type="button" aria-pressed={value === o.key} onClick={() => onChange(o.key)}>
          {o.label}
        </button>
      ))}
    </div>
  )
}

/** A long share's axis: a point or two of it is the whole month's move. */
const percentAxis = (v: number) => `${v.toFixed(1)}%`

function FuturesPositionsPanel({ latest }: { latest: ParticipantDay | undefined }) {
  return (
    <Panel title={latest ? `Index futures positions · ${formatDay(latest.date)}` : 'Index futures positions'} className="mf-card">
      {!latest ? (
        <EmptyState>No participant file stored yet.</EmptyState>
      ) : (
        <div className="tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th>Who</th>
                <th className="num">Long</th>
                <th className="num">Short</th>
                <th className="num">Net</th>
                <th className="num">Long share</th>
                <th className="num">Net change</th>
                <th>Stance</th>
              </tr>
            </thead>
            <tbody>
              {latest.groups.map((g) => {
                const stance = participantStance(g)
                return (
                  <tr key={g.clientType}>
                    <td>{g.clientType}</td>
                    <td className="num mono">{formatOi(g.futureIndexLong)}</td>
                    <td className="num mono">{formatOi(g.futureIndexShort)}</td>
                    <td className={`num mono ${signTone(g.futureIndexNet)}`}>{formatSignedContracts(g.futureIndexNet)}</td>
                    <td className="num mono">{g.futureIndexLongPercent != null ? `${g.futureIndexLongPercent.toFixed(1)}%` : '—'}</td>
                    <td className={`num mono ${signTone(g.futureIndexNetChange)}`}>{formatSignedContracts(g.futureIndexNetChange)}</td>
                    <td>
                      <Badge tone={stance.tone}>{stance.label}</Badge>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <p className="muted small">
        Contracts open at the end of the day in NIFTY, BANKNIFTY and other index futures. FII long share is the number
        desks quote: a low share means foreign funds are mostly short.
      </p>
    </Panel>
  )
}

type ChartOrTable = 'chart' | 'table'

const CHART_OR_TABLE: ReadonlyArray<{ key: ChartOrTable; label: string }> = [
  { key: 'chart', label: 'Chart' },
  { key: 'table', label: 'Table' },
]

/**
 * The day's index options by participant, calls and puts apart: a bar for
 * each net, or every number. NSE's file has carried these columns all
 * along; the page showed only the futures.
 */
function OptionsPositionsPanel({ latest, previous }: { latest: ParticipantDay | undefined; previous: ParticipantDay | undefined }) {
  const [view, setView] = useState<ChartOrTable>('chart')
  const rows = useMemo(() => optionsPositions(latest, previous), [latest, previous])
  const net = (value: number, change: number | null) => (
    <td className="num mono">
      {formatSignedContracts(value)}
      {change != null && <span className="mf-sub muted">{formatSignedContracts(change)}</span>}
    </td>
  )

  return (
    <Panel
      title={latest ? `Index options positions · ${formatDay(latest.date)}` : 'Index options positions'}
      className="mf-card"
      actions={rows.length > 0 && <ViewSwitch value={view} options={CHART_OR_TABLE} onChange={setView} label="Index options as" />}
    >
      {rows.length === 0 ? (
        <EmptyState>No participant file stored yet.</EmptyState>
      ) : view === 'chart' ? (
        <OptionsNetChart rows={rows} previousDay={previous?.date ?? null} />
      ) : (
        <>
          <div className="tablewrap">
            <table className="table mf-opt-table">
              <thead>
                <tr>
                  <th />
                  <th colSpan={3} className="mf-group">Index calls</th>
                  <th colSpan={3} className="mf-group">Index puts</th>
                </tr>
                <tr>
                  <th>Who</th>
                  <th className="num">Long</th>
                  <th className="num">Short</th>
                  <th className="num">Net</th>
                  <th className="num">Long</th>
                  <th className="num">Short</th>
                  <th className="num">Net</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.clientType}>
                    <td>{r.clientType}</td>
                    <td className="num mono">{formatOi(r.callLong)}</td>
                    <td className="num mono">{formatOi(r.callShort)}</td>
                    {net(r.callNet, r.callNetChange)}
                    <td className="num mono">{formatOi(r.putLong)}</td>
                    <td className="num mono">{formatOi(r.putShort)}</td>
                    {net(r.putNet, r.putNetChange)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      <p className="muted small">
        Contracts open at the end of the day in index options, bought (long) and written (short), calls and puts apart.
        Net is long less short{view === 'chart' ? ': right of the line net long, left net short, one scale for both' : ''}.
        Every contract has a buyer and a writer, so on each side the four nets add up to zero.
        {previous ? ` Under each net, its change since ${formatDay(previous.date)}.` : ''}
      </p>
    </Panel>
  )
}

type FuturesView = 'net' | 'longShare' | 'table'

const FUTURES_VIEWS: ReadonlyArray<{ key: FuturesView; label: string }> = [
  { key: 'net', label: 'Net' },
  { key: 'longShare', label: 'Long share' },
  { key: 'table', label: 'Table' },
]

/** FII index futures by session: net contracts as bars, or the long share as a line, or the table. */
function FiiFuturesPanel({ days }: { days: ParticipantDay[] }) {
  const [view, setView] = useState<FuturesView>('net')
  const trend = useMemo(() => futuresTrend(days), [days])
  const panes = useMemo<SessionPane[]>(
    () =>
      view === 'longShare'
        ? [{ key: 'long', label: 'Long share', kind: 'line', values: trend.map((p) => p.longPercent), baseline: 50 }]
        : [{ key: 'net', label: 'Net', kind: 'bars', values: trend.map((p) => p.net), baseline: 0 }],
    [trend, view],
  )
  const readout = (index: number) => {
    const p = trend[index]
    return (
      <>
        <span className="chart-readout__time mono">{formatDay(p.date)}</span>
        <span>net <b className="mono">{formatSignedContracts(p.net)}</b></span>
        <span>long share <b className="mono">{p.longPercent != null ? `${p.longPercent.toFixed(1)}%` : '—'}</b></span>
        <span>change <b className="mono">{formatSignedContracts(p.netChange)}</b></span>
      </>
    )
  }

  return (
    <Panel
      title={`FII net index futures · last ${trend.length} session${trend.length === 1 ? '' : 's'}`}
      className="mf-card"
      actions={<ViewSwitch value={view} options={FUTURES_VIEWS} onChange={setView} label="FII index futures as" />}
    >
      {trend.length === 0 ? (
        <EmptyState>No history yet.</EmptyState>
      ) : view === 'table' ? (
        // Twenty sessions scroll in place past eight, so this panel stays the
        // height of its neighbour.
        <div className={`tablewrap${days.length > 8 ? ' tablewrap--rows8' : ''}`}>
          <table className="table">
            <thead>
              <tr>
                <th>Day</th>
                <th className="num">Net</th>
                <th className="num">Long share</th>
                <th className="num">Change</th>
              </tr>
            </thead>
            <tbody>
              {[...trend].reverse().map((p) => (
                <tr key={p.date}>
                  <td>{formatDay(p.date)}</td>
                  <td className={`num mono ${signTone(p.net)}`}>{formatSignedContracts(p.net)}</td>
                  <td className="num mono">{p.longPercent != null ? `${p.longPercent.toFixed(1)}%` : '—'}</td>
                  <td className={`num mono ${signTone(p.netChange)}`}>{formatSignedContracts(p.netChange)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <SessionChart
          dates={trend.map((p) => p.date)}
          panes={panes}
          format={view === 'longShare' ? percentAxis : formatSignedContracts}
          label={view === 'longShare' ? 'FII long share of index futures by session' : 'FII net index futures contracts by session'}
          readout={readout}
        />
      )}
      <p className="muted small">
        {view === 'longShare'
          ? 'Long share: longs over longs and shorts, read against its own range. 50% is as many shorts as longs.'
          : view === 'net'
            ? 'Net: contracts long less contracts short at the day’s close. Under the zero line FIIs are net short.'
            : 'Net: contracts long less contracts short at the day’s close; change is against the session before.'}
      </p>
    </Panel>
  )
}

/** FII and DII cash-market buying less selling, a pane each on one scale, or the table. */
function CashPanel({ cash }: { cash: CashDay[] }) {
  const [view, setView] = useState<ChartOrTable>('chart')
  const fiiCash = cashStreak(cash, 'fii')
  const diiCash = cashStreak(cash, 'dii')
  const trend = useMemo(() => cashTrend(cash), [cash])
  const panes = useMemo<SessionPane[]>(
    () => [
      { key: 'fii', label: 'FII net', kind: 'bars', values: trend.map((d) => d.fii), baseline: 0 },
      { key: 'dii', label: 'DII net', kind: 'bars', values: trend.map((d) => d.dii), baseline: 0 },
    ],
    [trend],
  )
  const readout = (index: number) => {
    const d = trend[index]
    return (
      <>
        <span className="chart-readout__time mono">{formatDay(d.date)}</span>
        <span>FII net <b className="mono">{formatCrore(d.fii)}</b></span>
        <span>DII net <b className="mono">{formatCrore(d.dii)}</b></span>
      </>
    )
  }

  return (
    <Panel
      title="Cash market · FII and DII net (₹ crore)"
      className="mf-card"
      actions={cash.length > 0 && <ViewSwitch value={view} options={CHART_OR_TABLE} onChange={setView} label="Cash market as" />}
    >
      {cash.length === 0 ? (
        <EmptyState>No FII/DII figures stored yet. NSE publishes only the latest day, so history builds up from the day collection started.</EmptyState>
      ) : (
        <>
          <div className="mf-metrics mf-metrics--tight">
            <Metric
              label={`FII net · ${cash.length} day${cash.length === 1 ? '' : 's'}`}
              value={formatCrore(fiiCash.total)}
              sub={`${fiiCash.buyingDays} buying day${fiiCash.buyingDays === 1 ? '' : 's'} of ${fiiCash.days}`}
              tone={signTone(fiiCash.total)}
            />
            <Metric
              label={`DII net · ${cash.length} day${cash.length === 1 ? '' : 's'}`}
              value={formatCrore(diiCash.total)}
              sub={`${diiCash.buyingDays} buying day${diiCash.buyingDays === 1 ? '' : 's'} of ${diiCash.days}`}
              tone={signTone(diiCash.total)}
            />
          </div>
          {view === 'chart' ? (
            <SessionChart
              dates={trend.map((d) => d.date)}
              panes={panes}
              format={formatCrore}
              label="FII and DII net cash-market buying by day, in rupees crore"
              readout={readout}
            />
          ) : (
            <div className={`tablewrap${cash.length > 8 ? ' tablewrap--rows8' : ''}`}>
              <table className="table">
                <thead>
                  <tr>
                    <th>Day</th>
                    <th className="num">FII buy</th>
                    <th className="num">FII sell</th>
                    <th className="num">FII net</th>
                    <th className="num">DII buy</th>
                    <th className="num">DII sell</th>
                    <th className="num">DII net</th>
                  </tr>
                </thead>
                <tbody>
                  {cash.map((d) => (
                    <tr key={d.date}>
                      <td>{formatDay(d.date)}</td>
                      <td className="num mono">{grouped(d.fii?.buy)}</td>
                      <td className="num mono">{grouped(d.fii?.sell)}</td>
                      <td className={`num mono ${signTone(d.fii?.net)}`}>{formatCrore(d.fii?.net)}</td>
                      <td className="num mono">{grouped(d.dii?.buy)}</td>
                      <td className="num mono">{grouped(d.dii?.sell)}</td>
                      <td className={`num mono ${signTone(d.dii?.net)}`}>{formatCrore(d.dii?.net)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="muted small">Buying less selling in the cash market. Under the zero line the group sold more than it bought that day.</p>
        </>
      )}
    </Panel>
  )
}

/**
 * Two rows: where each group stood at the newest day's close, in futures and
 * in options; then how FIIs' futures and both groups' cash moved over the
 * sessions shown.
 */
function FlowsSection() {
  const flows = useMarketFlows(20)
  const data = flows.data

  return (
    <>
      <ResearchNote id="flows" />
      {flows.isError && <InlineError error={flows.error} />}
      {flows.isLoading && !data && <Loading />}

      {data && (
        <>
          <div className="mf-grid">
            <FuturesPositionsPanel latest={data.participants[0]} />
            <OptionsPositionsPanel latest={data.participants[0]} previous={data.participants[1]} />
          </div>
          <div className="mf-grid">
            <FiiFuturesPanel days={data.participants} />
            <CashPanel cash={data.cash} />
          </div>
        </>
      )}

      <StatusLine status={data?.status} datasets={['participant-oi', 'fii-dii-cash']} />
    </>
  )
}

// ---------------------------------------------------------------------------
// Global
// ---------------------------------------------------------------------------

function CueRow({ cue }: { cue: GlobalCue }) {
  return (
    <tr>
      <td>{cue.name}</td>
      <td className="num mono">{cue.error ? '—' : grouped(cue.lastPrice, 2)}</td>
      <td className={`num mono ${signTone(cue.change)}`}>{cue.error ? '—' : grouped(cue.change, 2)}</td>
      <td className={`num mono ${signTone(cue.changePercent)}`}>{cue.error ? '—' : movePercent(cue.changePercent)}</td>
      <td className="muted small">{cue.error ? <span className="neg">{cue.error}</span> : formatDateTime(cue.asOfUtc)}</td>
    </tr>
  )
}

function GlobalSection() {
  const cues = useGlobalCues()
  const data = cues.data
  const gap = gapReading(data?.indicatedGapPercent)
  const groups = [...new Set((data?.markets ?? []).map((m) => m.group))]

  return (
    <>
      <ResearchNote id="global" />
      {cues.isError && <InlineError error={cues.error} />}
      {cues.isLoading && !data && <Loading />}

      {data && (
        <>
          <div className="mf-metrics">
            <Metric
              label="GIFT Nifty"
              value={data.gift.error ? '—' : grouped(data.gift.lastPrice, 2)}
              sub={
                data.gift.error
                  ? data.gift.error
                  : `${movePercent(data.gift.changePercent)} · ${formatDateTime(data.gift.asOfUtc)}${data.giftExpiry ? ` · exp ${formatDay(data.giftExpiry)}` : ''}`
              }
              tone={signTone(data.gift.changePercent)}
            />
            <Metric
              label="Against NSE NIFTY future's last close"
              value={data.indicatedGapPoints != null ? `${data.indicatedGapPoints > 0 ? '+' : ''}${grouped(data.indicatedGapPoints, 1)}` : '—'}
              sub={
                data.nseFutureClose != null
                  ? `${gap.label} · close ${grouped(data.nseFutureClose, 2)} on ${data.nseFutureCloseDate ? formatDay(data.nseFutureCloseDate) : '—'}`
                  : 'no NSE close of the same contract stored yet'
              }
              tone={gap.tone}
            />
          </div>

          <div className="mf-grid">
            {groups.map((g) => (
              <Panel key={g} title={g} className="mf-card">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Market</th>
                      <th className="num">Last</th>
                      <th className="num">Change</th>
                      <th className="num">%</th>
                      <th>As of</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.markets
                      .filter((m) => m.group === g)
                      .map((m) => (
                        <CueRow key={m.symbol} cue={m} />
                      ))}
                  </tbody>
                </table>
              </Panel>
            ))}
          </div>
          <p className="muted small">
            {data.sourceNote} Snapshot taken {formatAge(data.fetchedUtc)}; it refreshes every two minutes.
          </p>
        </>
      )}
    </>
  )
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

function EventRow({ e, canDelete, onDelete }: { e: MarketEvent; canDelete: boolean; onDelete: (id: number) => void }) {
  const tone = e.kind === 'holiday' ? 'warn' : e.kind === 'expiry' ? 'neutral' : e.importance >= 3 ? 'neg' : 'accent'
  return (
    <li className="mf-event">
      <span className="mf-event__time mono small">{e.timeIst ?? ''}</span>
      <span className="mf-event__body">
        <span className="mf-event__title">{e.title}</span>
        <span className="mf-event__meta small muted">
          <Badge tone={tone}>{e.category}</Badge> {e.region}
          {e.importance >= 3 && e.kind === 'event' ? ' · can move the market a lot' : ''}
          {e.notes ? ` · ${e.notes}` : ''}
          {e.source && e.kind === 'event' && (
            <>
              {' · '}
              <a href={e.source} target="_blank" rel="noreferrer">
                source
              </a>
            </>
          )}
        </span>
      </span>
      {canDelete && e.id != null && (
        <button type="button" className="btn btn--sm btn--ghost" onClick={() => onDelete(e.id!)} aria-label={`Remove ${e.title}`}>
          Remove
        </button>
      )}
    </li>
  )
}

function AddEventForm() {
  const add = useAddMarketEvent()
  const [form, setForm] = useState({ date: istDate(), timeIst: '', region: 'IN', category: 'Other', title: '', importance: 2, source: '', notes: '' })

  function submit(ev: FormEvent) {
    ev.preventDefault()
    add.mutate(
      {
        date: form.date,
        timeIst: form.timeIst || null,
        region: form.region,
        category: form.category,
        title: form.title,
        importance: form.importance,
        source: form.source || null,
        notes: form.notes || null,
      },
      { onSuccess: () => setForm((f) => ({ ...f, title: '', notes: '', source: '' })) },
    )
  }

  return (
    <Panel title="Add an event" className="mf-card">
      <form className="mf-form" onSubmit={submit}>
        <input className="field__input field__input--sm" type="date" value={form.date} onChange={(e) => setForm({ ...form, date: e.target.value })} required aria-label="Date" />
        <input className="field__input field__input--sm" type="time" value={form.timeIst} onChange={(e) => setForm({ ...form, timeIst: e.target.value })} aria-label="Time (IST)" />
        <select className="field__input field__input--sm" value={form.region} onChange={(e) => setForm({ ...form, region: e.target.value })} aria-label="Region">
          <option value="IN">India</option>
          <option value="US">United States</option>
          <option value="GLOBAL">Global</option>
        </select>
        <select className="field__input field__input--sm" value={form.category} onChange={(e) => setForm({ ...form, category: e.target.value })} aria-label="Category">
          {EVENT_CATEGORIES.map((c) => (
            <option key={c}>{c}</option>
          ))}
        </select>
        <input className="field__input field__input--sm mf-form__title" placeholder="What happens" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} required aria-label="Title" />
        <select className="field__input field__input--sm" value={form.importance} onChange={(e) => setForm({ ...form, importance: Number(e.target.value) })} aria-label="Importance">
          <option value={1}>Worth knowing</option>
          <option value={2}>Moves the market</option>
          <option value={3}>Can move it a lot</option>
        </select>
        <input className="field__input field__input--sm mf-form__source" placeholder="Source link (optional)" value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value })} aria-label="Source" />
        <button type="submit" className="btn btn--sm btn--primary" disabled={add.isPending}>
          {add.isPending ? 'Adding…' : 'Add'}
        </button>
      </form>
      {add.isError && <InlineError error={add.error} />}
    </Panel>
  )
}

function EventsSection() {
  const today = istDate()
  const events = useMarketEvents()
  const remove = useDeleteMarketEvent()
  const { user } = useAuth()
  const isAdmin = user?.role === 'Admin'
  const parts = splitEvents(events.data?.events, today)

  const day = (date: string, list: MarketEvent[]) => (
    <div key={date} className="mf-day">
      <div className="mf-day__date">{formatDay(date)}</div>
      <ul className="mf-events">
        {list.map((e, i) => (
          <EventRow key={`${e.kind}-${e.id ?? i}-${e.title}`} e={e} canDelete={isAdmin && e.kind === 'event'} onDelete={(id) => remove.mutate(id)} />
        ))}
      </ul>
    </div>
  )

  return (
    <>
      <ResearchNote id="events" />
      {events.isError && <InlineError error={events.error} />}
      {remove.isError && <InlineError error={remove.error} />}
      {events.isLoading && !events.data && <Loading />}

      {events.data && (
        <div className="mf-grid">
          <Panel title="Today and the next 30 days" className="mf-card">
            {parts.today.length === 0 && parts.upcoming.length === 0 ? (
              <EmptyState>Nothing scheduled.</EmptyState>
            ) : (
              <>
                {parts.today.length > 0 && day(today, parts.today)}
                {parts.upcoming.map(([date, list]) => day(date, list))}
              </>
            )}
          </Panel>
          <div className="mf-stack">
            <Panel title="Last 7 days" className="mf-card">
              {parts.past.length === 0 ? <EmptyState>Nothing in the last week.</EmptyState> : parts.past.map(([date, list]) => day(date, list))}
            </Panel>
            {isAdmin && <AddEventForm />}
          </div>
        </div>
      )}
    </>
  )
}

// ---------------------------------------------------------------------------

export default function MarketFactorsPage() {
  const [params, setParams] = useSearchParams()
  const section = sectionFrom(params.get('section'))
  const moved = movedFactorSection(params.get('section'))
  if (moved) return <Navigate to={moved} replace />

  return (
    <div className="page mf">
      <header className="page__header">
        <div>
          <h1 className="page__title">Market factors</h1>
          <p className="page__subtitle">
            What desks read to judge where the market may go. Each section says how fresh it is and what our own tests found.
          </p>
        </div>
      </header>

      <div className="oc-tabs" role="tablist" aria-label="Section">
        {SECTIONS.map((s) => (
          <button
            key={s.key}
            type="button"
            role="tab"
            aria-selected={section === s.key}
            className={`oc-tab ${section === s.key ? 'oc-tab--on' : ''}`}
            onClick={() => setParams({ section: s.key }, { replace: true })}
          >
            {s.label}
          </button>
        ))}
      </div>

      <div role="tabpanel" className="mf-body">
        {section === 'flows' && <FlowsSection />}
        {section === 'global' && <GlobalSection />}
        {section === 'events' && <EventsSection />}
      </div>
    </div>
  )
}
