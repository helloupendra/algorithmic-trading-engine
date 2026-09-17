/**
 * Market factors: the numbers desks read to judge where the market may go,
 * gathered on one page.
 *
 * Five sections, one at a time (the choice lives in the URL):
 *  - Levels: OI walls, max pain, PCR, IV, VIX and the move the straddle prices,
 *    read live from the same chain view the option chain page uses;
 *  - Futures build-up: index futures today (live OI against yesterday's close)
 *    and over recent sessions, plus the day's stock futures by build-up;
 *  - FII & DII: cash-market buying and selling, and how FIIs, DIIs, pros and
 *    clients sit in index futures, from NSE's evening files;
 *  - Global: GIFT Nifty and the overseas markets that set the tone before 09:15;
 *  - Events: RBI and Fed decisions, US inflation prints, expiries and holidays.
 *
 * Every section says how fresh its numbers are and what our own tests found for
 * that factor, so a reading is never mistaken for a proven signal.
 */

import { useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  useAddMarketEvent,
  useDeleteMarketEvent,
  useGlobalCues,
  useMarketEvents,
  useMarketFlows,
  useMarketFutures,
  useOptionChainView,
  useSyncMarketFactors,
} from '../../lib/queries'
import {
  BUILD_UPS,
  DATASET_LABELS,
  EVENT_CATEGORIES,
  LEVEL_UNDERLYINGS,
  RESEARCH_NOTES,
  cashStreak,
  distanceTo,
  expectedMove,
  formatCrore,
  formatDay,
  formatDistance,
  formatSignedContracts,
  gapReading,
  istDate,
  participantStance,
  signTone,
  splitEvents,
  topWalls,
} from '../../lib/factors'
import type { DatasetStatus, GlobalCue, MarketEvent, WallRow } from '../../lib/factors'
import { buildUpTone, formatOi, movePercent } from '../../lib/movers'
import { formatAge, formatDateTime } from '../../lib/format'
import { useAuth } from '../../lib/auth'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import './movers.css'
import './factors.css'

const SECTIONS = [
  { key: 'levels', label: 'Levels' },
  { key: 'futures', label: 'Futures build-up' },
  { key: 'flows', label: 'FII & DII' },
  { key: 'global', label: 'Global' },
  { key: 'events', label: 'Events' },
] as const
type SectionKey = (typeof SECTIONS)[number]['key']

function sectionFrom(value: string | null): SectionKey {
  return (SECTIONS.find((s) => s.key === value)?.key ?? 'levels') as SectionKey
}

function ResearchNote({ id }: { id: keyof typeof RESEARCH_NOTES }) {
  const note = RESEARCH_NOTES[id]
  return (
    <p className="mf-research small">
      <Badge tone={note.tested ? 'accent' : 'neutral'}>{note.tested ? 'Tested' : 'Not tested yet'}</Badge> {note.text}
    </p>
  )
}

function Metric({ label, value, sub, tone }: { label: string; value: ReactNode; sub?: ReactNode; tone?: string }) {
  return (
    <div className="mf-metric">
      <div className="mf-metric__label">{label}</div>
      <div className={`mf-metric__value mono ${tone ?? ''}`}>{value}</div>
      {sub != null && <div className="mf-metric__sub small muted">{sub}</div>}
    </div>
  )
}

const num = (v: number | null | undefined, digits = 0) =>
  v == null || Number.isNaN(v) ? '—' : v.toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })

// ---------------------------------------------------------------------------
// Levels
// ---------------------------------------------------------------------------

function WallsTable({ title, rows, tone }: { title: string; rows: WallRow[]; tone: 'pos' | 'neg' }) {
  return (
    <Panel title={title} className="mf-card">
      {rows.length === 0 ? (
        <EmptyState>No open interest in this chain.</EmptyState>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th>Strike</th>
              <th className="num">Open interest</th>
              <th className="num">Change today</th>
              <th className="num">From spot</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((w, i) => (
              <tr key={w.strike}>
                <td className={`mono ${i === 0 ? tone : ''}`}>{w.strike.toLocaleString('en-IN')}</td>
                <td className="num mono">{formatOi(w.openInterest)}</td>
                <td className={`num mono ${signTone(w.openInterestChange)}`}>{formatSignedContracts(w.openInterestChange)}</td>
                <td className="num mono">{formatDistance(w.distance)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

function LevelsSection() {
  const [underlying, setUnderlying] = useState<string>('NIFTY')
  const view = useOptionChainView(underlying)
  const chain = view.data
  const header = chain?.header ?? null
  const spot = header?.spot?.lastPrice ?? chain?.spotPrice ?? null
  const move = expectedMove(chain)

  return (
    <>
      <div className="mf-tools">
        <div className="mv-filters" role="group" aria-label="Underlying" style={{ margin: 0 }}>
          {LEVEL_UNDERLYINGS.map((u) => (
            <button key={u} type="button" className="mv-filter" aria-pressed={underlying === u} onClick={() => setUnderlying(u)}>
              {u}
            </button>
          ))}
        </div>
        <span className="muted small">
          {header
            ? `${header.mode === 'live' ? 'Live' : 'Last capture'} · ${formatAge(header.liveOverlayUtc ?? header.snapshotCapturedUtc)}${
                chain?.expiryDate ? ` · expiry ${chain.expiryDate}` : ''
              }`
            : view.isLoading
              ? 'Loading the chain…'
              : ''}
        </span>
      </div>
      <ResearchNote id="levels" />

      {view.isError && <InlineError error={view.error} />}
      {view.isLoading && !chain && <Loading />}
      {chain && !header && <EmptyState>No chain has been captured for {underlying} yet.</EmptyState>}

      {chain && header && (
        <>
          <div className="mf-metrics">
            <Metric
              label={underlying}
              value={num(spot, 2)}
              sub={header.spot?.changePercent != null ? movePercent(header.spot.changePercent) : undefined}
              tone={signTone(header.spot?.changePercent)}
            />
            <Metric
              label="Resistance · biggest call OI"
              value={num(header.resistanceStrike)}
              sub={`${formatOi(header.resistanceOpenInterest)} · ${formatDistance(distanceTo(spot, header.resistanceStrike))}`}
              tone="neg"
            />
            <Metric
              label="Support · biggest put OI"
              value={num(header.supportStrike)}
              sub={`${formatOi(header.supportOpenInterest)} · ${formatDistance(distanceTo(spot, header.supportStrike))}`}
              tone="pos"
            />
            <Metric label="Max pain" value={num(header.maxPainStrike)} sub={formatDistance(distanceTo(spot, header.maxPainStrike))} />
            <Metric
              label="Put-call ratio"
              value={header.putCallRatio != null ? header.putCallRatio.toFixed(2) : '—'}
              sub={header.putCallRatioOfChange != null ? `of today's change ${header.putCallRatioOfChange.toFixed(2)}` : 'above 1: more puts written'}
            />
            <Metric
              label="Move the straddle prices"
              value={move ? `±${num(move.points)}` : '—'}
              sub={move ? `${move.percent.toFixed(2)}% to expiry · ${num(move.strike)} straddle` : 'no ATM prices'}
            />
            <Metric label="ATM IV" value={header.atTheMoneyIv != null ? `${header.atTheMoneyIv.toFixed(1)}%` : '—'} />
            <Metric
              label="India VIX"
              value={num(header.vix?.lastPrice, 2)}
              sub={header.vix?.changePercent != null ? movePercent(header.vix.changePercent) : undefined}
              tone={signTone(header.vix?.changePercent) === 'pos' ? 'neg' : signTone(header.vix?.changePercent) === 'neg' ? 'pos' : ''}
            />
            <Metric label="Days to expiry" value={header.daysToExpiry ?? '—'} />
          </div>

          <div className="mf-grid">
            <WallsTable title="Call walls · where sellers expect a ceiling" rows={topWalls(chain, 'call')} tone="neg" />
            <WallsTable title="Put walls · where sellers expect a floor" rows={topWalls(chain, 'put')} tone="pos" />
          </div>
          <p className="muted small">
            A wall is where option sellers have the most contracts open. Desks read the biggest call OI as resistance and the
            biggest put OI as support; walls move during the day as positions are added and closed.
          </p>
        </>
      )}
    </>
  )
}

// ---------------------------------------------------------------------------
// Futures build-up
// ---------------------------------------------------------------------------

function StatusLine({ status, datasets }: { status: DatasetStatus[] | undefined; datasets: string[] }) {
  const { user } = useAuth()
  const sync = useSyncMarketFactors()
  const rows = datasets.map((d) => status?.find((s) => s.dataset === d) ?? null)

  return (
    <div className="mf-status small">
      {datasets.map((d, i) => {
        const s = rows[i]
        return (
          <span key={d} className="mf-status__item">
            <span className={`live-dot ${s?.lastFailed ? 'neg' : s?.lastSuccessUtc ? 'pos' : ''}`} aria-hidden />
            {DATASET_LABELS[d] ?? d}:{' '}
            {s
              ? `${s.newestDay ? `newest ${formatDay(s.newestDay)}` : 'nothing stored yet'} · checked ${formatAge(s.lastAttemptUtc)}${
                  s.lastFailed && s.lastMessage ? ` · ${s.lastMessage}` : ''
                }`
              : 'not fetched since the API started'}
          </span>
        )
      })}
      {user?.role === 'Admin' && (
        <button type="button" className="btn btn--sm" onClick={() => sync.mutate()} disabled={sync.isPending}>
          {sync.isPending ? 'Fetching from NSE…' : 'Fetch now'}
        </button>
      )}
      {sync.isError && <InlineError error={sync.error} />}
    </div>
  )
}

function FuturesSection() {
  const futures = useMarketFutures(10)
  const data = futures.data

  return (
    <>
      <ResearchNote id="futures" />
      {futures.isError && <InlineError error={futures.error} />}
      {futures.isLoading && !data && <Loading />}
      {data && !data.latestDay && (
        <EmptyState>No bhavcopy is stored yet. The API fetches NSE's files a few minutes after it starts and every evening after 18:00 IST.</EmptyState>
      )}

      {data?.latestDay && (
        <>
          <Panel title="Index futures · price and open interest together" className="mf-card">
            <table className="table">
              <thead>
                <tr>
                  <th>Future</th>
                  <th>Now (live vs last close)</th>
                  <th className="num">Price</th>
                  <th className="num">OI</th>
                  <th>Last session · {formatDay(data.latestDay)}</th>
                  <th className="num">Price</th>
                  <th className="num">OI</th>
                  <th>Last 10 sessions (newest left)</th>
                </tr>
              </thead>
              <tbody>
                {data.indices.map((f) => (
                  <tr key={f.underlying}>
                    <td>
                      {f.underlying}
                      {f.latest && <span className="cell-sub muted small">exp {formatDay(f.latest.expiry)}</span>}
                    </td>
                    <td>
                      {f.live?.buildUp ? (
                        <>
                          <Badge tone={buildUpTone(f.live.buildUp)}>{f.live.buildUp}</Badge>
                          <span className="cell-sub muted small">
                            {f.live.isFresh ? 'live' : `quote ${formatAge(f.live.asOfUtc)}`}
                          </span>
                        </>
                      ) : (
                        <span className="muted small">{f.live ? 'no baseline close yet' : 'no live quote'}</span>
                      )}
                    </td>
                    <td className={`num mono ${signTone(f.live?.priceChangePercent)}`}>{movePercent(f.live?.priceChangePercent)}</td>
                    <td className={`num mono ${signTone(f.live?.openInterestChangePercent)}`}>{movePercent(f.live?.openInterestChangePercent)}</td>
                    <td>{f.latest ? <Badge tone={buildUpTone(f.latest.buildUp)}>{f.latest.buildUp}</Badge> : '—'}</td>
                    <td className={`num mono ${signTone(f.latest?.priceChangePercent)}`}>{movePercent(f.latest?.priceChangePercent)}</td>
                    <td className={`num mono ${signTone(f.latest?.openInterestChangePercent)}`}>{movePercent(f.latest?.openInterestChangePercent)}</td>
                    <td>
                      <div className="mf-strip">
                        {f.history.slice(0, 10).map((h) => (
                          <span
                            key={h.date}
                            className={`mf-strip__cell mf-tone--${buildUpTone(h.buildUp)}`}
                            title={`${formatDay(h.date)}: ${h.buildUp} · price ${movePercent(h.priceChangePercent)} · OI ${movePercent(h.openInterestChangePercent)}`}
                          />
                        ))}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="muted small">
              Long build-up: price and OI both up (new buyers). Short build-up: price down, OI up (new sellers). Short covering:
              price up, OI down (sellers leaving). Long unwinding: both down (buyers leaving).
            </p>
          </Panel>

          <h2 className="mf-h2">Stock futures · {formatDay(data.latestDay)}</h2>
          <div className="mf-grid mf-grid--four">
            {BUILD_UPS.map((b) => (
              <Panel key={b} title={`${b} · ${data.stockCounts[b] ?? 0}`} className="mf-card">
                {(data.stocks[b] ?? []).length === 0 ? (
                  <EmptyState>None.</EmptyState>
                ) : (
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Stock</th>
                        <th className="num">Price</th>
                        <th className="num">OI</th>
                      </tr>
                    </thead>
                    <tbody>
                      {data.stocks[b].map((s) => (
                        <tr key={s.underlying}>
                          <td>{s.underlying}</td>
                          <td className={`num mono ${signTone(s.priceChangePercent)}`}>{movePercent(s.priceChangePercent)}</td>
                          <td className={`num mono ${signTone(s.openInterestChangePercent)}`}>{movePercent(s.openInterestChangePercent)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </Panel>
            ))}
          </div>
        </>
      )}

      <StatusLine status={data?.status} datasets={['futures-bhavcopy']} />
    </>
  )
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
                          <div className="mf-bar">
                            <span
                              className={`mf-bar__fill ${(h.fii?.futureIndexLongPercent ?? 0) >= 50 ? 'pos' : 'neg'}`}
                              style={{ width: `${h.fii?.futureIndexLongPercent ?? 0}%` }}
                            />
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
                        <td className="num mono">{num(d.fii?.buy)}</td>
                        <td className="num mono">{num(d.fii?.sell)}</td>
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
      <td className="num mono">{cue.error ? '—' : num(cue.lastPrice, 2)}</td>
      <td className={`num mono ${signTone(cue.change)}`}>{cue.error ? '—' : num(cue.change, 2)}</td>
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
              value={data.gift.error ? '—' : num(data.gift.lastPrice, 2)}
              sub={
                data.gift.error
                  ? data.gift.error
                  : `${movePercent(data.gift.changePercent)} · ${formatDateTime(data.gift.asOfUtc)}${data.giftExpiry ? ` · exp ${formatDay(data.giftExpiry)}` : ''}`
              }
              tone={signTone(data.gift.changePercent)}
            />
            <Metric
              label="Against NSE NIFTY future's last close"
              value={data.indicatedGapPoints != null ? `${data.indicatedGapPoints > 0 ? '+' : ''}${num(data.indicatedGapPoints, 1)}` : '—'}
              sub={
                data.nseFutureClose != null
                  ? `${gap.label} · close ${num(data.nseFutureClose, 2)} on ${data.nseFutureCloseDate ? formatDay(data.nseFutureCloseDate) : '—'}`
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

  return (
    <div className="page mf">
      <header className="mv-head">
        <div>
          <h1>Market factors</h1>
          <p className="muted small mv-head__meta">
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
        {section === 'levels' && <LevelsSection />}
        {section === 'futures' && <FuturesSection />}
        {section === 'flows' && <FlowsSection />}
        {section === 'global' && <GlobalSection />}
        {section === 'events' && <EventsSection />}
      </div>
    </div>
  )
}
