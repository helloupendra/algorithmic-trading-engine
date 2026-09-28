/**
 * Market factors: the numbers desks read to judge where the market may go,
 * gathered on one page.
 *
 * Three sections, one at a time (the choice lives in the URL):
 *  - FII & DII: cash-market buying and selling, and how FIIs, DIIs, pros and
 *    clients sit in index futures, from NSE's evening files;
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

import { useState } from 'react'
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
  formatCrore,
  formatDay,
  formatSignedContracts,
  gapReading,
  grouped,
  istDate,
  movedFactorSection,
  participantStance,
  signTone,
  splitEvents,
} from '../../lib/factors'
import type { GlobalCue, MarketEvent } from '../../lib/factors'
import { formatOi, movePercent } from '../../lib/movers'
import { formatAge, formatDateTime } from '../../lib/format'
import { useAuth } from '../../lib/auth'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { Metric, ResearchNote, StatusLine } from '../markets/factorParts'
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

function FlowsSection() {
  const flows = useMarketFlows(20)
  const data = flows.data
  const latest = data?.participants[0]
  const fiiHistory = (data?.participants ?? []).map((d) => ({ date: d.date, fii: d.groups.find((g) => g.clientType === 'FII') }))
  const fiiCash = cashStreak(data?.cash ?? [], 'fii')
  const diiCash = cashStreak(data?.cash ?? [], 'dii')
  const maxCash = Math.max(1, ...(data?.cash ?? []).flatMap((d) => [Math.abs(d.fii?.net ?? 0), Math.abs(d.dii?.net ?? 0)]))

  return (
    <>
      <ResearchNote id="flows" />
      {flows.isError && <InlineError error={flows.error} />}
      {flows.isLoading && !data && <Loading />}

      {data && (
        <>
          <div className="mf-grid">
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

            <Panel title="FII net index futures · last sessions" className="mf-card">
              {fiiHistory.length === 0 ? (
                <EmptyState>No history yet.</EmptyState>
              ) : (
                // Twenty sessions scroll in place past eight, so this panel
                // is the height of its neighbour and the cash market stays on
                // the first screen.
                <div className={`tablewrap${fiiHistory.length > 8 ? ' tablewrap--rows8' : ''}`}>
                <table className="table">
                  <thead>
                    <tr>
                      <th>Day</th>
                      <th className="num">Net</th>
                      <th>Long share</th>
                      <th className="num">Change</th>
                    </tr>
                  </thead>
                  <tbody>
                    {fiiHistory.map((h) => (
                      <tr key={h.date}>
                        <td>{formatDay(h.date)}</td>
                        <td className={`num mono ${signTone(h.fii?.futureIndexNet)}`}>{formatSignedContracts(h.fii?.futureIndexNet)}</td>
                        <td>
                          <div className="mf-bar-read">
                            <div className="mf-bar">
                              <span
                                className={`mf-bar__fill ${(h.fii?.futureIndexLongPercent ?? 0) >= 50 ? 'pos' : 'neg'}`}
                                style={{ width: `${h.fii?.futureIndexLongPercent ?? 0}%` }}
                              />
                            </div>
                            <span className="mf-bar__label mono small">
                              {h.fii?.futureIndexLongPercent != null ? `${h.fii.futureIndexLongPercent.toFixed(1)}%` : '—'}
                            </span>
                          </div>
                        </td>
                        <td className={`num mono ${signTone(h.fii?.futureIndexNetChange)}`}>{formatSignedContracts(h.fii?.futureIndexNetChange)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                </div>
              )}
            </Panel>
          </div>

          <Panel title="Cash market · FII and DII buying and selling (₹ crore)" className="mf-card">
            {data.cash.length === 0 ? (
              <EmptyState>No FII/DII figures stored yet. NSE publishes only the latest day, so history builds up from the day collection started.</EmptyState>
            ) : (
              <>
                <div className="mf-metrics mf-metrics--tight">
                  <Metric
                    label={`FII net · ${data.cash.length} day${data.cash.length === 1 ? '' : 's'}`}
                    value={formatCrore(fiiCash.total)}
                    sub={`${fiiCash.buyingDays} buying day${fiiCash.buyingDays === 1 ? '' : 's'} of ${fiiCash.days}`}
                    tone={signTone(fiiCash.total)}
                  />
                  <Metric
                    label={`DII net · ${data.cash.length} day${data.cash.length === 1 ? '' : 's'}`}
                    value={formatCrore(diiCash.total)}
                    sub={`${diiCash.buyingDays} buying day${diiCash.buyingDays === 1 ? '' : 's'} of ${diiCash.days}`}
                    tone={signTone(diiCash.total)}
                  />
                </div>
                <div className={`tablewrap${data.cash.length > 8 ? ' tablewrap--rows8' : ''}`}>
                <table className="table">
                  <thead>
                    <tr>
                      <th>Day</th>
                      <th className="num">FII buy</th>
                      <th className="num">FII sell</th>
                      <th className="num">FII net</th>
                      <th />
                      <th className="num">DII net</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {data.cash.map((d) => (
                      <tr key={d.date}>
                        <td>{formatDay(d.date)}</td>
                        <td className="num mono">{grouped(d.fii?.buy)}</td>
                        <td className="num mono">{grouped(d.fii?.sell)}</td>
                        <td className={`num mono ${signTone(d.fii?.net)}`}>{formatCrore(d.fii?.net)}</td>
                        <td className="mf-barcell">
                          <div className="mf-bar">
                            <span className={`mf-bar__fill ${(d.fii?.net ?? 0) >= 0 ? 'pos' : 'neg'}`} style={{ width: `${(Math.abs(d.fii?.net ?? 0) / maxCash) * 100}%` }} />
                          </div>
                        </td>
                        <td className={`num mono ${signTone(d.dii?.net)}`}>{formatCrore(d.dii?.net)}</td>
                        <td className="mf-barcell">
                          <div className="mf-bar">
                            <span className={`mf-bar__fill ${(d.dii?.net ?? 0) >= 0 ? 'pos' : 'neg'}`} style={{ width: `${(Math.abs(d.dii?.net ?? 0) / maxCash) * 100}%` }} />
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                </div>
              </>
            )}
          </Panel>
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
