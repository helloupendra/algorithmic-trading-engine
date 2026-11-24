/**
 * AI → AI Trader: the AI Trader's own desk on one page (24 Nov 2026).
 *
 * The owner: "mujhe ek pura section chaiye jismai ai trader trade karta hua
 * dikhey aur apna sb manage krey". Top to bottom the page shows:
 * - whether it is working now, today's net and how much loss it may still take;
 * - what it is doing (its last look, in its own words);
 * - its trades and the scripts it runs;
 * - every look of the day.
 *
 * Practice: a replay it was asked into (Data → Replay) plays a recorded day,
 * and its decisions and shadow trades on that day show here too. That is how it
 * can be watched trading when the market is shut. A practice trade places nothing.
 *
 * The AI Trader runs in its own ₹5 lakh paper account ("ai-trader"). It buys
 * NIFTY, BANKNIFTY and SENSEX options, and may start any option-buying script
 * for itself. Nothing reaches a broker.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Badge, EmptyState, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { formatInr, formatInrSigned, formatInrWhole, formatTime } from '../../lib/format'
import { strategyLabel } from '../../lib/desk'
import {
  AI_TRADER_POLL_MS,
  AI_TRADER_REPLAY_POLL_MS,
  contractText,
  limitsParts,
  lotsText,
  netTone,
  shadowEnding,
  shadowLastPrice,
} from '../../lib/aiTrader'
import type { AiTraderDecision, AiTraderShadowPosition } from '../../lib/aiTrader'
import { contractName, lookBadge, nowText } from '../../lib/aiTraderDesk'
import type { AiTraderDesk, AiTraderDeskPosition, AiTraderDeskRun } from '../../lib/aiTraderDesk'
import { useAiTraderDecisions, useAiTraderDesk, useAiTraderPositions, useReplayStatus } from '../../lib/queries'
import './ai.css'
import './aiTrader.css'
import './aiTraderPage.css'

const ACTIVE = new Set(['starting', 'waiting', 'playing', 'paused'])

function todayIst(): string {
  return new Date(Date.now() + 5.5 * 3600_000).toISOString().slice(0, 10)
}

function shortDate(iso: string): string {
  const d = new Date(`${iso}T00:00:00Z`)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleDateString('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' })
}

export function AiTraderPage() {
  const desk = useAiTraderDesk()
  const replay = useReplayStatus()
  const session = replay.data?.session ?? null
  const practice = session?.aiTrader && session.id != null ? session : null
  const practiceActive = !!practice && ACTIVE.has(practice.state)
  const [picked, setPicked] = useState<'live' | 'practice' | null>(null)
  const view: 'live' | 'practice' = picked ?? (practiceActive ? 'practice' : 'live')

  return (
    <div className="page hp ai atp">
      <header className="page__header">
        <div>
          <h1 className="page__title">AI Trader</h1>
          <p className="page__subtitle">
            Its own ₹5,00,000 paper account. It reads the market every few minutes, buys and sells NIFTY, BANKNIFTY and SENSEX
            options, and may start any option-buying script for itself, inside fixed limits. Nothing reaches a broker.
          </p>
        </div>
        <span className="sys-asof faint">refreshes every 30 s</span>
      </header>

      {desk.isPending ? (
        <Loading label="Reading the AI Trader's account…" />
      ) : !desk.data ? (
        <InlineError error={desk.error} />
      ) : (
        <>
          <Status d={desk.data} />
          {practice && (
            <div className="atp-modes" role="tablist" aria-label="Which day to show">
              <button
                type="button"
                role="tab"
                aria-selected={view === 'live'}
                className={`atp-modes__tab${view === 'live' ? ' is-on' : ''}`}
                onClick={() => setPicked('live')}
              >
                Its account today
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'practice'}
                className={`atp-modes__tab${view === 'practice' ? ' is-on' : ''}`}
                onClick={() => setPicked('practice')}
              >
                Practice: {shortDate(practice.date)}
                {practiceActive ? ` · ${practice.clockIst ?? 'starting'}` : ` · ${practice.state}`}
              </button>
            </div>
          )}
          {view === 'practice' && practice ? (
            <Practice id={practice.id!} date={practice.date} clockIst={practice.clockIst} active={practiceActive} speed={practice.speed} />
          ) : (
            <Live d={desk.data} />
          )}
        </>
      )}
    </div>
  )
}

function Status({ d }: { d: AiTraderDesk }) {
  const now = nowText(d)
  const limit = d.rules?.dailyLossLimit ?? null
  const maxTrades = d.rules?.maxTradesPerDay ?? null
  const maxRuns = d.rules?.maxStrategyRuns ?? null
  const running = d.runs.filter((r) => r.running).length
  return (
    <>
      <p className={`atp-now atp-now--${now.tone}`}>
        <Badge tone={d.status === 'on' ? (d.openNow ? 'live' : 'accent') : 'neutral'}>{d.status === 'on' ? 'On' : 'Off'}</Badge>
        <span>{now.text}</span>
      </p>
      <div className="stat-grid atp-stats">
        <StatTile
          label="Today's net"
          value={formatInrSigned(d.netToday)}
          tone={d.netToday == null || d.netToday === 0 ? undefined : d.netToday > 0 ? 'pos' : 'neg'}
          sub={d.chargesToday ? `after ${formatInr(d.chargesToday)} of charges` : 'after charges'}
        />
        <StatTile label="Trades today" value={`${d.openedToday}${maxTrades != null ? ` of ${maxTrades}` : ''}`} sub="buys it placed" />
        <StatTile
          label="Loss it may still take"
          value={formatInrWhole(d.lossLeft)}
          tone={d.lossLeft != null && limit != null && d.lossLeft < limit / 2 ? 'warn' : undefined}
          sub={limit != null ? `of ${formatInrWhole(limit)} a day; then no new trades` : undefined}
        />
        <StatTile label="Scripts running" value={`${running}${maxRuns != null ? ` of ${maxRuns}` : ''}`} sub="started by itself" />
      </div>
    </>
  )
}

// ---------------------------------------------------------------- its account (live)

function Live({ d }: { d: AiTraderDesk }) {
  const day = todayIst()
  const decisions = useAiTraderDecisions({ day }, AI_TRADER_POLL_MS)
  const looks = useMemo(() => decisions.data?.pages.flatMap((p) => p.items) ?? [], [decisions.data])
  return (
    <>
      <Panel title="What it is doing">
        {d.lastLook ? <LastLook l={d.lastLook} /> : <p className="small-note ai-flush">No look yet. Its first one comes at {d.rules?.openFrom ?? '09:20'} IST on a trading day.</p>}
      </Panel>

      <Panel
        title="Its trades"
        actions={
          d.accountId != null ? (
            <Link className="btn btn--ghost btn--sm" to={`/trade/orders?user=${d.accountId}`}>
              Every order
            </Link>
          ) : undefined
        }
      >
        {d.positions.length === 0 ? (
          <EmptyState>No trade today yet. When it buys, the trade shows here with its stop, target and running result.</EmptyState>
        ) : (
          <LiveTrades rows={d.positions} looks={looks} />
        )}
      </Panel>

      <Panel title="Scripts it runs">
        {d.runs.length === 0 ? (
          <p className="small-note ai-flush">None today. It may start any of the scripts below when it judges the day suits one.</p>
        ) : (
          <Runs rows={d.runs} />
        )}
        <Scripts d={d} />
      </Panel>

      <Panel title={`Every look today (${looks.length})`}>
        {decisions.isPending ? <Loading label="Reading today's looks…" /> : <Looks rows={looks} empty="No look today yet." />}
      </Panel>

      {d.rules && (
        <p className="small-note atp-limits">
          <b>Its limits</b>, enforced by code, not by the model: {limitsParts({ ...d.rules, strategies: [] }).join(' · ')}.
        </p>
      )}
    </>
  )
}

function LastLook({ l }: { l: AiTraderDecision }) {
  const b = lookBadge(l)
  return (
    <div className="atp-last">
      <div className="atp-last__head">
        <span className="atp-time">{l.clockIst}</span>
        <Badge tone={b.tone}>{b.label}</Badge>
        {l.placed && <span className="faint">{l.placed.text}</span>}
      </div>
      <p className="atp-last__why">“{l.reason || l.why || 'No reason given.'}”</p>
      {!l.allowed && l.why && <p className="small-note ai-flush">Refused because: {l.why}</p>}
    </div>
  )
}

/** One trade as the page draws it, from its live book or a practice book. */
interface TradeView {
  key: number
  contract: string
  lots: string
  boughtIst: string
  entry: number | null
  /** Now (open) or the sale (closed). */
  price: number | null
  stop: number | null
  target: number | null
  result: number | null
  resultNote: string
  open: boolean
  /** "Sold 13:07 · target hit" for a closed one. */
  ending: string
  whyIn: string | null
  whyOut: string | null
}

function whyOf(looks: AiTraderDecision[], test: (l: AiTraderDecision) => boolean): string | null {
  const l = looks.find(test)
  return l ? l.reason || l.why || null : null
}

function liveTrade(p: AiTraderDeskPosition, looks: AiTraderDecision[]): TradeView {
  return {
    key: p.id,
    contract: contractName(p.symbol),
    lots: p.lots != null ? `${p.lots} ${p.lots === 1 ? 'lot' : 'lots'}` : '',
    boughtIst: formatTime(p.openedUtc).slice(0, 5),
    entry: p.entry,
    price: p.mark,
    stop: p.stopLoss,
    target: p.target,
    result: p.pnl,
    resultNote: 'before charges',
    open: p.open,
    ending: p.open ? '' : `Sold ${formatTime(p.closedUtc).slice(0, 5)}`,
    whyIn: whyOf(looks, (l) => l.action === 'buy' && l.placed?.positionId === p.id),
    whyOut: whyOf(looks, (l) => l.action === 'exit' && l.placed?.positionId === p.id),
  }
}

function LiveTrades({ rows, looks }: { rows: AiTraderDeskPosition[]; looks: AiTraderDecision[] }) {
  return (
    <div className="atp-trades">
      {rows.map((p) => (
        <TradeCard key={p.id} t={liveTrade(p, looks)} />
      ))}
    </div>
  )
}

/** Where a price sits between the stop (0) and the target (1); null when it cannot be placed. */
function along(price: number | null, stop: number | null, target: number | null): number | null {
  if (price == null || stop == null || target == null || target <= stop) return null
  return Math.min(1, Math.max(0, (price - stop) / (target - stop)))
}

function TradeCard({ t }: { t: TradeView }) {
  const at = along(t.price, t.stop, t.target)
  const entryAt = along(t.entry, t.stop, t.target)
  const tone = netTone(t.result)
  return (
    <article className={`atp-trade${t.open ? ' is-open' : ''}`}>
      <header className="atp-trade__head">
        <div>
          <div className="atp-trade__what">
            {t.contract}
            {t.lots && <span className="faint"> · {t.lots}</span>}
          </div>
          <div className="faint atp-trade__when">
            Bought {t.boughtIst} at {formatInr(t.entry)}
            {t.open ? ' · still open' : ` · ${t.ending}`}
          </div>
        </div>
        <div className="atp-trade__result">
          <div className={`atp-trade__money ${tone}`}>{formatInrSigned(t.result)}</div>
          <div className="faint atp-trade__note">{t.open ? 'if sold now, ' : ''}{t.resultNote}</div>
        </div>
      </header>

      {at != null && (
        <div className="atp-bar" aria-label={`Price ${formatInr(t.price)} between the stop ${formatInr(t.stop)} and the target ${formatInr(t.target)}`}>
          <div className="atp-bar__track">
            {entryAt != null && <span className="atp-bar__entry" style={{ left: `${entryAt * 100}%` }} />}
            <span className={`atp-bar__now ${tone}`} style={{ left: `${at * 100}%` }} />
          </div>
          <div className="atp-bar__labels">
            <span className="neg">Stop {formatInr(t.stop)}</span>
            <span>
              {t.open ? 'Now' : 'Sold at'} <b>{formatInr(t.price)}</b>
            </span>
            <span className="pos">Target {formatInr(t.target)}</span>
          </div>
        </div>
      )}

      {t.whyIn && (
        <p className="atp-trade__why">
          <span className="atp-trade__label">Why it bought</span>“{t.whyIn}”
        </p>
      )}
      {t.whyOut && (
        <p className="atp-trade__why">
          <span className="atp-trade__label">Why it sold</span>“{t.whyOut}”
        </p>
      )}
    </article>
  )
}

function Runs({ rows }: { rows: AiTraderDeskRun[] }) {
  return (
    <div className="tablewrap">
      <table className="table atp-table">
        <thead>
          <tr>
            <th>Script</th>
            <th>Index</th>
            <th>Started</th>
            <th>Status</th>
            <th className="num">Net</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.runId}>
              <td>
                <Link to={`/trade/runs/${r.runId}`}>{strategyLabel(r.strategy)}</Link>
              </td>
              <td>{r.underlying || '—'}</td>
              <td className="atp-time">{formatTime(r.startedUtc).slice(0, 5)}</td>
              <td>{r.running ? <Badge tone="live">Running</Badge> : <span className="faint">{r.status || 'Stopped'}</span>}</td>
              <td className={`num ${netTone(r.net)}`}>{formatInrSigned(r.net)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Scripts({ d }: { d: AiTraderDesk }) {
  const names = d.rules?.strategies ?? []
  if (names.length === 0) return null
  return (
    <div className="atp-scripts">
      <h3 className="atp-sub">Scripts it may start ({names.length}, at most {d.rules?.maxStrategyRuns ?? 3} at once)</h3>
      <ul className="atp-scripts__list">
        {names.map((n) => (
          <li key={n}>
            <b>{strategyLabel(n)}</b>
            {d.strategyNotes[n] && <span className="faint"> · {d.strategyNotes[n]}</span>}
          </li>
        ))}
      </ul>
    </div>
  )
}

function Looks({ rows, empty }: { rows: AiTraderDecision[]; empty: string }) {
  const [open, setOpen] = useState<number | null>(null)
  if (rows.length === 0) return <p className="small-note ai-flush">{empty}</p>
  return (
    <ol className="atp-looks">
      {rows.map((l) => {
        const b = lookBadge(l)
        const isOpen = open === l.id
        return (
          <li key={l.id} className={`atp-look${isOpen ? ' is-open' : ''}`}>
            <button type="button" className="atp-look__btn" aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : l.id)}>
              <span className="atp-time">{l.clockIst}</span>
              <Badge tone={b.tone}>{b.label}</Badge>
              <span className="atp-look__why">{l.reason || l.why || '—'}</span>
            </button>
            {isOpen && (
              <div className="atp-look__more">
                {l.placed && <p className="ai-flush">{l.placed.text}</p>}
                {!l.allowed && l.why && <p className="ai-flush">Refused because: {l.why}</p>}
                <p className="small-note ai-flush">
                  Model {l.model || '—'}
                  {l.confidence != null ? ` · it was ${Math.round(l.confidence * 100)}% sure` : ''}
                </p>
              </div>
            )}
          </li>
        )
      })}
    </ol>
  )
}

// ---------------------------------------------------------------- practice (a replay)

function Practice({ id, date, clockIst, active, speed }: { id: number; date: string; clockIst: string | null; active: boolean; speed: number | null }) {
  const poll = active ? AI_TRADER_REPLAY_POLL_MS : false
  const book = useAiTraderPositions({ replay: id }, poll)
  const decisions = useAiTraderDecisions({ replay: id }, poll)
  const looks = useMemo(() => decisions.data?.pages.flatMap((p) => p.items) ?? [], [decisions.data])
  return (
    <>
      <p className="atp-practice">
        <b>Practice on {shortDate(date)}</b>: that day&apos;s recorded market plays again{speed ? ` at ${speed}× speed` : ''}
        {active && clockIst ? `, now at ${clockIst} on that day` : ''}. It decides exactly as it would live; its trades go to a practice book and
        place nothing. Started from <Link to="/data/replay">Data → Replay</Link>.
      </p>
      <Panel title={`Its practice trades${book.data?.net != null ? ` · net ${formatInrSigned(book.data.net)}` : ''}`}>
        {book.isPending ? (
          <Loading label="Reading the practice book…" />
        ) : !book.data || book.data.items.length === 0 ? (
          <EmptyState>No trade yet on this day. When it buys, the trade shows here and moves with the replay.</EmptyState>
        ) : (
          <PracticeTrades rows={book.data.items} looks={looks} />
        )}
      </Panel>
      <Panel title={`Every look on ${shortDate(date)} (${looks.length})`}>
        {decisions.isPending ? <Loading label="Reading its looks…" /> : <Looks rows={looks} empty="No look yet: the first one comes a few minutes after the replay's open." />}
      </Panel>
    </>
  )
}

function practiceTrade(p: AiTraderShadowPosition, looks: AiTraderDecision[]): TradeView {
  const last = shadowLastPrice(p)
  const end = shadowEnding(p)
  return {
    key: p.id,
    contract: contractText(p),
    lots: lotsText(p.lots, p.lotSize),
    boughtIst: p.entryIst || '—',
    entry: p.entryPrice,
    price: last.price,
    stop: p.stopLoss,
    target: p.target,
    result: p.net,
    resultNote: 'after charges',
    open: p.open,
    ending: `Sold ${p.exitIst || ''} · ${end.label.toLowerCase()}`.replace('  ', ' '),
    whyIn: whyOf(looks, (l) => l.id === p.decisionId),
    whyOut: p.exitReason === 'exit' ? whyOf(looks, (l) => l.action === 'exit' && l.allowed && (l.clockIst ?? '') >= (p.exitIst ?? '')) : null,
  }
}

function PracticeTrades({ rows, looks }: { rows: AiTraderShadowPosition[]; looks: AiTraderDecision[] }) {
  return (
    <div className="atp-trades">
      {rows.map((p) => (
        <TradeCard key={p.id} t={practiceTrade(p, looks)} />
      ))}
      <p className="small-note ai-flush">Net after charges; an open trade as if sold at its last price.</p>
    </div>
  )
}
