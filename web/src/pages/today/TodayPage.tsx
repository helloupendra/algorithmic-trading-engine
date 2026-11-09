/**
 * Today (/today): the owner's one page, and an admin's home.
 *
 * The owner asked on 1 Oct for one section where he sees everything, because
 * he is busy and cannot keep answering yes/no. So this page is read-first:
 * nothing on it needs a click to keep the desk going, and it asks nothing.
 * Its links only open the page that has a thing in full.
 *
 * 7 Nov: drawn as seven long sections one under another, it opened on a wall
 * of detail, and the owner asked for a calmer, professional first screen. It
 * now opens on an
 * Overview that fits about one screen: the day in six figures (net P&L, live
 * runs, open legs, what needs a look, the AI Trader, open incidents), then
 * trading by account with the worst runs, what needs a look, the markets,
 * the agents and the system, each short. The long parts are whole under the
 * tabs: Trading (every run and its stop reason), AI (the AI Trader, every
 * agent, learning) and Decisions (the log). The tab is in the address
 * (?tab=), so a link or a reload opens the same view.
 *
 * One request (GET /api/Today, lib/today.ts), read again every 30 s. A part
 * the API left out says so in words; it is never drawn as an empty part.
 * Money is signed and grouped the Indian way, times are IST, and every
 * colour also says what it means in words or an icon.
 */

import { useState } from 'react'
import type { ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { CHECK_PASS_MARK, checkScoreText, memoryHref, memoryLabel, shortDate } from '../../lib/ai'
import { actionText, modeLabel, netTone } from '../../lib/aiTrader'
import { ApiError } from '../../lib/api'
import { verdictBadge } from '../../lib/checkup'
import { istDay, strategyLabel } from '../../lib/desk'
import { deploySummary } from '../../lib/deploys'
import { formatDateTime, formatInrSigned, formatInrWhole, formatPercent, formatPrice, formatTime } from '../../lib/format'
import { useMarketPulse } from '../../lib/queries'
import { shortStopReason } from '../../lib/runHistory'
import type { MarketPulseItem } from '../../lib/types'
import {
  TODAY_POLL_MS,
  TODAY_TABS,
  agentTotals,
  agentTrouble,
  agoText,
  attentionCounts,
  attentionKind,
  attentionLevel,
  attentionTone,
  dayTotals,
  decisionStatus,
  examText,
  istWhen,
  longDate,
  marketText,
  plural,
  recapText,
  runStatus,
  sentinelState,
  todayTab,
  useToday,
} from '../../lib/today'
import type {
  AttentionItem,
  CheckDay,
  CheckScore,
  Today,
  TodayAccount,
  TodayAgent,
  TodayAiTrader,
  TodayDecision,
  TodayLearning,
  TodayRun,
  TodaySystem,
  TodayTab,
  TodayTrading,
} from '../../lib/today'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { IconAlert, IconCandles, IconChip, IconInfo, IconLayers, IconPen, IconServer, IconSwitch, IconWarning } from '../../components/icons'
import { StatusPill } from '../ai/parts'
import { AiTraderVerdict } from '../ai/AiTraderParts'
import { errorText, useNow } from '../ai/common'
import '../system/health/health.css'
import '../ai/ai.css'
import './today.css'

/** The full IST stamp, for a title: "01 Oct, 11:02:15 IST". */
const fullIst = (iso: string | null) => (iso ? `${formatDateTime(iso)} IST` : undefined)

/** A rupee figure's tone, as the figure is printed (rounded): a ₹0 day is not green. */
function moneyTone(v: number | null): 'pos' | 'neg' | undefined {
  if (v == null) return undefined
  const r = Math.round(v)
  return r > 0 ? 'pos' : r < 0 ? 'neg' : undefined
}

/** A signed figure, "+₹1,250" / "−₹640": the sign says it, the colour repeats it. "—" when not known. */
function Rupees({ value }: { value: number | null }) {
  return <span className={`tdy-n ${moneyTone(value) ?? ''}`}>{formatInrSigned(value)}</span>
}

/** A part the API did not send: not known, which is not the same as empty. */
function Missing({ what }: { what: string }) {
  return (
    <p className="small-note small-note--warn tdy-flush" role="status">
      {what} did not come back from the API, so it is not known right now (not empty). The page reads again every 30 s.
    </p>
  )
}

// ---------- 1. the date and the markets -------------------------------------------------

function Header({ d, updatedAt, refreshFailed, now }: { d: Today | undefined; updatedAt: number; refreshFailed: boolean; now: number }) {
  const date = d?.date || istDay(now)
  return (
    <header className="hp-bar tdy-head">
      <div className="tdy-head__main">
        <h1 className="tdy-date">{longDate(date)}</h1>
        {d && (
          <div className="tdy-markets" aria-label="Markets">
            {d.markets === null ? (
              <span className="small-note small-note--warn tdy-flush">The market states did not come back.</span>
            ) : d.markets.length === 0 ? (
              <span className="faint">No market sessions reported.</span>
            ) : (
              d.markets.map((m) => {
                const { text, tone } = marketText(m, now)
                return (
                  <span key={m.exchange} className={`pill ai-pill tdy-pill${tone === 'pos' ? ' pill--pos' : ''}`}>
                    <span className="pill__dot" aria-hidden="true" />
                    {text}
                  </span>
                )
              })
            )}
          </div>
        )}
      </div>
      <span className="sys-asof">
        <span className="faint">
          {updatedAt > 0 && <>as of {formatTime(new Date(updatedAt).toISOString())} IST · </>}refreshes every {TODAY_POLL_MS / 1000} s ·
          read-only
        </span>
        {refreshFailed && d && (
          <span className="warn" role="status">
            The last refresh failed: showing what was read at {formatTime(new Date(updatedAt).toISOString())}.
          </span>
        )}
      </span>
    </header>
  )
}

// ---------- the day in six figures ---------------------------------------------------------

/**
 * One figure of the strip. A figure with more behind it opens it: a tab of
 * this page (a button) or another page (a link); the strip is one bordered
 * row, its cells split by hairlines, so the six read as one thing.
 */
function Kpi({
  label,
  value,
  tone,
  sub,
  open,
  to,
  hint,
}: {
  label: string
  value: ReactNode
  tone?: string
  sub?: ReactNode
  open?: () => void
  to?: string
  hint?: string
}) {
  const body = (
    <>
      <span className="tdy-kpi__label">{label}</span>
      <span className={`tdy-kpi__value ${tone ?? ''}`}>{value}</span>
      {sub && <span className="tdy-kpi__sub">{sub}</span>}
    </>
  )
  if (open) {
    return (
      <button type="button" className="tdy-kpi tdy-kpi--go" onClick={open} title={hint}>
        {body}
      </button>
    )
  }
  if (to) {
    return (
      <Link className="tdy-kpi tdy-kpi--go" to={to} title={hint}>
        {body}
      </Link>
    )
  }
  return <div className="tdy-kpi">{body}</div>
}

function Kpis({ d, now, go }: { d: Today; now: number; go: (t: TodayTab) => void }) {
  const t = d.trading
  const totals = t ? dayTotals(t) : null
  const items = d.attention
  const s = d.system
  const ai = d.aiTrader
  const sentinel = s ? sentinelState(s.sentinelLastUtc, now) : null
  const verdict = s?.checkup ? verdictBadge({ status: 'done', verdict: s.checkup.verdict }) : null
  const total = totals ? totals.live + totals.stopped : 0
  return (
    <section className="tdy-kpis" aria-label="The day in figures">
      <Kpi
        label="Run net P&L"
        value={totals ? formatInrSigned(totals.net) : '—'}
        tone={totals ? moneyTone(totals.net) : undefined}
        open={() => go('trading')}
        hint="Full-run realized + open P&L − charges, including carried books. This is not the change since midnight. Opens Trading."
        sub={
          !totals ? (
            <span className="warn">not known: trading did not come back</span>
          ) : totals.accounts === 0 ? (
            'no live run today'
          ) : (
            <>
              realized net <Rupees value={totals.realizedNet} /> · open <Rupees value={totals.unrealized} /> · charges <span className="tdy-n">{formatInrWhole(totals.charges)}</span>
              {totals.partial && <span className="warn"> · some accounts not known</span>}
            </>
          )
        }
      />
      <Kpi
        label="Live runs"
        value={totals ? totals.live : '—'}
        open={() => go('trading')}
        hint="Runs trading now, of today's live runs. Opens Trading."
        sub={
          totals && total > 0 ? (
            <>
              of {total} · {totals.stopped} stopped
              {totals.failed > 0 && <span className="neg"> · {totals.failed} failed</span>}
            </>
          ) : totals ? (
            'none started today'
          ) : undefined
        }
      />
      <Kpi
        label="Open legs"
        value={totals ? totals.openLegs : '—'}
        to="/trade/positions"
        hint="Positions open now across the accounts. Opens Positions."
        sub={totals ? (totals.accounts > 0 ? `across ${plural(totals.accounts, 'account')}` : 'no account traded') : undefined}
      />
      <Kpi
        label="Needs a look"
        value={items ? items.length : '—'}
        tone={items && items.length > 0 ? attentionTone(items) : undefined}
        sub={
          items === null ? (
            <span className="warn">not known</span>
          ) : items.length === 0 ? (
            <span className="pos">nothing needs you</span>
          ) : (
            attentionCounts(items)
              .map((c) => `${c.n} ${c.label.toLowerCase()}`)
              .join(' · ')
          )
        }
      />
      <Kpi
        label={ai?.live ? 'AI Trader · live net' : 'AI Trader'}
        value={!ai ? 'Off' : ai.live ? formatInrSigned(ai.live.net) : ai.status === 'on' ? 'On' : ai.status === 'off' ? 'Off' : ai.status}
        tone={ai?.live ? netTone(ai.live.net) : undefined}
        open={() => go('ai')}
        hint="The AI Trader's day: its paper account, its looks and what the checks refused. Opens AI."
        sub={
          !ai ? (
            'not switched on'
          ) : (
            <>
              {ai.mode ? modeLabel(ai.mode).label : `status ${ai.status}`} · {plural(ai.actions, 'action')}
              {ai.refused > 0 && <span className="warn"> · {ai.refused} refused</span>}
            </>
          )
        }
      />
      <Kpi
        label="Open incidents"
        value={s?.openIncidents ?? '—'}
        tone={s?.openIncidents ? 'warn' : undefined}
        to="/system/incidents"
        hint="Incidents Sentinel raised that are still open. Opens Incidents."
        sub={
          !s ? (
            <span className="warn">the system did not come back</span>
          ) : (
            <>
              {verdict ? `checkup ${verdict.label.toLowerCase()}` : 'no checkup yet'}
              {sentinel && (
                <span className={sentinel.late ? 'warn' : ''}> · Sentinel {sentinel.late ? 'late' : agoText(s.sentinelLastUtc, now)}</span>
              )}
            </>
          )
        }
      />
    </section>
  )
}

// ---------- needs a look ------------------------------------------------------------------

function LevelMark({ level }: { level: string }) {
  const l = attentionLevel(level)
  const Icon = l.key === 'critical' || l.key === 'high' ? IconWarning : l.key === 'medium' ? IconAlert : IconInfo
  return (
    <span className={`tdy-level tdy-level--${l.tone}`}>
      <Icon aria-hidden="true" />
      {l.label}
    </span>
  )
}

/** One line a row, the title the link to where it is handled; its detail in two lines under it. */
function AttentionRow({ item, now }: { item: AttentionItem; now: number }) {
  const tone = attentionLevel(item.level).tone
  const kind = attentionKind(item.kind)
  const at = istWhen(item.atUtc, now)
  return (
    <li className={`tdy-att__row tdy-att__row--${tone}`}>
      <LevelMark level={item.level} />
      <div className="tdy-att__body">
        <div className="tdy-att__title">
          {item.link ? (
            <Link to={item.link} className="tdy-att__link">
              {item.title}
            </Link>
          ) : (
            item.title
          )}
        </div>
        {item.detail && (
          <div className="tdy-att__detail" title={item.detail}>
            {item.detail}
          </div>
        )}
      </div>
      <div className="tdy-att__meta">
        {kind && <span>{kind}</span>}
        {at && <span title={fullIst(item.atUtc)}>{at}</span>}
      </div>
    </li>
  )
}

/** The overview shows the most serious few; the rest unfold in place. */
const ATTENTION_SHOWN = 6

function Attention({ items, now }: { items: AttentionItem[] | null; now: number }) {
  const [all, setAll] = useState(false)
  const shown = items && !all ? items.slice(0, ATTENTION_SHOWN) : items
  const hidden = items ? items.length - ATTENTION_SHOWN : 0
  return (
    <Panel
      className="tdy-attention"
      title={
        <>
          <IconAlert /> Needs a look
          {items && items.length > 0 && <span className="faint ai-title-note">{plural(items.length, 'item')}, most serious first</span>}
        </>
      }
    >
      {items === null ? (
        <Missing what="The list of what needs a look" />
      ) : items.length === 0 ? (
        <p className="tdy-calm">Nothing needs you right now.</p>
      ) : (
        <>
          <ul className="tdy-att">{shown!.map((item, i) => <AttentionRow key={`${item.level}-${item.title}-${i}`} item={item} now={now} />)}</ul>
          {hidden > 0 && (
            <button type="button" className="tdy-fold" onClick={() => setAll(!all)} aria-expanded={all}>
              {all ? 'Show the most serious only' : `Show ${plural(hidden, 'more item')}`}
            </button>
          )}
        </>
      )}
    </Panel>
  )
}

// ---------- trading today ----------------------------------------------------------------

function PnlParts({ realizedNet, unrealized }: { realizedNet: number | null; unrealized: number | null }) {
  return (
    <div className="tdy-pnl-parts faint">
      <span>Realized net <Rupees value={realizedNet} /></span>
      <span>Open <Rupees value={unrealized} /></span>
    </div>
  )
}

/** Each account's day in one row, and their total under them when there is more than one. */
function AccountsTable({ accounts }: { accounts: TodayAccount[] }) {
  const totals = dayTotals({ accounts, runs: [], recapsToday: 0 })
  return (
    <div className="tablewrap tdy-accts">
      <table className="table">
        <caption className="tdy-sr">Each account's full-run P&L, including carried books</caption>
        <thead>
          <tr>
            <th>Account</th>
            <th className="num">Total net</th>
            <th className="num tdy-wide">Realized gross</th>
            <th className="num tdy-wide">Charges</th>
            <th className="num">Live</th>
            <th className="num">Stopped</th>
            <th className="num">Open legs</th>
          </tr>
        </thead>
        <tbody>
          {accounts.map((a) => (
            <tr key={a.userName}>
              <td className="tdy-acct__name">{a.userName}</td>
              <td className="num tdy-strong">
                <Rupees value={a.net} />
                <PnlParts realizedNet={a.realizedNet} unrealized={a.unrealized} />
              </td>
              <td className="num tdy-wide">
                <Rupees value={a.gross} />
              </td>
              <td className="num tdy-wide">{formatInrWhole(a.charges)}</td>
              <td className="num">{a.runsLive}</td>
              <td className="num">{a.runsStopped}</td>
              <td className="num">{a.openLegs}</td>
            </tr>
          ))}
        </tbody>
        {accounts.length > 1 && (
          <tfoot>
            <tr>
              <td>All accounts</td>
              <td className="num tdy-strong">
                <Rupees value={totals.net} />
                <PnlParts realizedNet={totals.realizedNet} unrealized={totals.unrealized} />
                {totals.partial && <span className="warn" title="Some account sent no net; this is the sum of the ones that did."> *</span>}
              </td>
              <td className="num tdy-wide">
                <Rupees value={totals.gross} />
              </td>
              <td className="num tdy-wide">{formatInrWhole(totals.charges)}</td>
              <td className="num">{totals.live}</td>
              <td className="num">{totals.stopped}</td>
              <td className="num">{totals.openLegs}</td>
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  )
}

function RunRow({ r, now, reasons }: { r: TodayRun; now: number; reasons: boolean }) {
  const s = runStatus(r.status)
  const reason = shortStopReason(r.stopReason)
  const stopped = istWhen(r.stoppedUtc, now)
  return (
    <tr>
      <td>
        <Link
          className="tdy-run"
          to={`/trade/runs/${r.runId}`}
          title={`Run ${r.runId}, started ${fullIst(r.startedUtc) ?? 'at an unknown time'}`}
        >
          {strategyLabel(r.strategy)}
        </Link>
      </td>
      <td>{r.underlying || <span className="faint">—</span>}</td>
      <td>{r.userName || <span className="faint">—</span>}</td>
      <td>
        <Badge tone={s.tone}>{s.label}</Badge>
      </td>
      <td className="num">{r.trades}</td>
      <td className="num">
        <Rupees value={r.net} />
        <PnlParts realizedNet={r.realizedNet} unrealized={r.unrealized} />
      </td>
      {reasons && (
        <td className="tdy-reason" title={r.stopReason ?? undefined}>
          {reason ? (
            <>
              {stopped && <span className="faint">{stopped} · </span>}
              {reason}
            </>
          ) : (
            <span className="faint">—</span>
          )}
        </td>
      )}
    </tr>
  )
}

function RunsTable({ runs, now, caption, reasons }: { runs: TodayRun[]; now: number; caption: string; reasons: boolean }) {
  return (
    <div className="tablewrap tdy-runs">
      <table className="table">
        <caption className="tdy-caption">{caption}</caption>
        <thead>
          <tr>
            <th>Strategy</th>
            <th>Index</th>
            <th>Account</th>
            <th>Status</th>
            <th className="num">Trades</th>
            <th className="num">Total net</th>
            {reasons && <th>Stop reason</th>}
          </tr>
        </thead>
        <tbody>
          {runs.map((r) => (
            <RunRow key={r.runId} r={r} now={now} reasons={reasons} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * The same runs as a list, for a phone: the net beside the name on the first
 * line, where a sideways-scrolling table would have hidden it past the edge.
 */
function RunList({ runs, now, caption }: { runs: TodayRun[]; now: number; caption: string }) {
  return (
    <div className="tdy-rl">
      <p className="tdy-rl__cap">{caption}</p>
      <ol className="tdy-rl__list">
        {runs.map((r) => {
          const s = runStatus(r.status)
          const reason = shortStopReason(r.stopReason)
          const stopped = istWhen(r.stoppedUtc, now)
          return (
            <li key={r.runId} className="tdy-rl__row">
              <div className="tdy-rl__head">
                <Link className="tdy-run" to={`/trade/runs/${r.runId}`}>
                  {strategyLabel(r.strategy)}
                </Link>
                <span className="faint">{r.underlying}</span>
                <span className="tdy-rl__net">
                  <Rupees value={r.net} />
                </span>
              </div>
              <PnlParts realizedNet={r.realizedNet} unrealized={r.unrealized} />
              <div className="tdy-rl__meta">
                <Badge tone={s.tone}>{s.label}</Badge>
                <span>{r.userName}</span>
                <span>{plural(r.trades, 'trade')}</span>
                {reason && (
                  <span className="tdy-rl__reason" title={r.stopReason ?? undefined}>
                    {stopped && `${stopped} · `}
                    {reason}
                  </span>
                )}
              </div>
            </li>
          )
        })}
      </ol>
    </div>
  )
}

/** The overview shows the worst few runs; Trading has them all. */
const RUNS_SHOWN = 8

/**
 * Trading today. On the overview (`brief`): the accounts and the worst
 * {@link RUNS_SHOWN} runs, with the way to the rest; on the Trading tab, every
 * run with its stop reason.
 */
function Trading({ t, now, brief, go }: { t: TodayTrading | null; now: number; brief?: boolean; go?: (t: TodayTab) => void }) {
  const recaps = t ? recapText(t.recapsToday) : ''
  const runs = t ? (brief ? t.runs.slice(0, RUNS_SHOWN) : t.runs) : []
  const caption =
    t && brief && t.runs.length > RUNS_SHOWN
      ? `The worst ${RUNS_SHOWN} of ${t.runs.length} runs by net`
      : `${plural(t?.runs.length ?? 0, 'run')}, worst total net first`
  return (
    <Panel
      className="tdy-trading"
      title={
        <>
          <IconSwitch /> Trading today <span className="faint ai-title-note">live runs only</span>
        </>
      }
      actions={
        brief && go && t && t.runs.length > 0 ? (
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => go('trading')}>
            All {plural(t.runs.length, 'run')} →
          </button>
        ) : (
          <Link className="btn btn--sm btn--ghost" to="/trade/runs">
            Runs page →
          </Link>
        )
      }
    >
      {t === null ? (
        <Missing what="Today's trading" />
      ) : (
        <>
          {t.accounts.length === 0 && t.runs.length === 0 ? (
            <EmptyState>No runs today or carried positions.</EmptyState>
          ) : (
            <>
              <p className="small-note">Full-run P&L for today’s runs and carried books, including positions closed today. Total net = realized net + open P&L; this is not the change since midnight.</p>
              {t.accounts.length > 0 && <AccountsTable accounts={t.accounts} />}
              {runs.length > 0 && <RunsTable runs={runs} now={now} caption={caption} reasons={!brief} />}
              {runs.length > 0 && <RunList runs={runs} now={now} caption={caption} />}
            </>
          )}
          {recaps && <p className="small-note">{recaps}</p>}
        </>
      )}
    </Panel>
  )
}

// ---------- the markets ------------------------------------------------------------------

function PulseRow({ i }: { i: MarketPulseItem }) {
  const tone = i.change == null ? '' : i.change > 0 ? 'pos' : i.change < 0 ? 'neg' : ''
  const change = i.change == null ? '—' : `${i.change > 0 ? '+' : i.change < 0 ? '−' : ''}${formatPrice(Math.abs(i.change))}`
  return (
    <tr>
      <td>
        <span className="tdy-mkt__name">{i.name}</span>
        {i.contract && <span className="faint tdy-mkt__ct"> {i.contract}</span>}
      </td>
      <td className="num tdy-strong">{formatPrice(i.lastTradedPrice)}</td>
      <td className={`num ${tone}`}>{change}</td>
      <td className={`num ${tone}`}>{i.changePercent == null ? '—' : formatPercent(i.changePercent).replace('-', '−')}</td>
    </tr>
  )
}

/**
 * The indices and commodities of the market pulse (the same tiles as the
 * Desk and the watchlist, moved by pushed ticks), as one short table.
 * Large caps stay on Markets.
 */
function Markets({ now }: { now: number }) {
  const pulse = useMarketPulse()
  const groups = (pulse.data?.groups ?? []).filter((g) => g.key !== 'equity' && g.items.length > 0)
  const asOf = istWhen(pulse.data?.latestQuoteUtc ?? null, now)
  return (
    <Panel
      className="tdy-markets-panel"
      title={
        <>
          <IconCandles /> Markets {asOf && <span className="faint ai-title-note">last quote {asOf}</span>}
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/markets">
          Markets →
        </Link>
      }
    >
      {pulse.isPending ? (
        <p className="tdy-none">Reading prices…</p>
      ) : !pulse.data ? (
        <p className="small-note small-note--warn tdy-flush">The prices could not be read: {errorText(pulse.error)}</p>
      ) : groups.length === 0 ? (
        <p className="tdy-none">No index or commodity is on the feed.</p>
      ) : (
        <table className="table tdy-mkt">
          <caption className="tdy-sr">Indices and commodities: last price and change on the previous close</caption>
          <thead>
            <tr>
              <th>Name</th>
              <th className="num">Last</th>
              <th className="num">Change</th>
              <th className="num">%</th>
            </tr>
          </thead>
          {groups.map((g) => (
            <tbody key={g.key}>
              {g.items.map((i) => (
                <PulseRow key={i.symbol} i={i} />
              ))}
            </tbody>
          ))}
        </table>
      )}
    </Panel>
  )
}

// ---------- 3b. the AI Trader ------------------------------------------------------------------

/**
 * The AI Trader today: on or off, shadow or placing, its looks and their
 * verdicts, and the last three decisions with the model's reason. Shown once
 * it has been switched on or has looked; its record is on AI → Agents.
 */
function AiTrader({ t, now }: { t: TodayAiTrader; now: number }) {
  const mode = modeLabel(t.mode)
  const figures: [string, number, string][] = [
    ['Looks', t.decisions, ''],
    ['Actions', t.actions, ''],
    ['Allowed', t.allowed, t.allowed > 0 ? 'pos' : ''],
    ['Refused', t.refused, t.refused > 0 ? 'warn' : ''],
    ['No answer', t.noAnswer, ''],
  ]
  return (
    <Panel
      className="atr-today"
      title={
        <>
          <IconChip /> AI Trader
          <StatusPill status={t.status} />
          {t.mode && (
            <span className="faint ai-title-note atr-today-mode">
              {mode.label}
              {mode.means ? `: ${mode.means}` : ''}
            </span>
          )}
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/agents?agent=ai-trader">
          Its decisions →
        </Link>
      }
    >
      <div className="tdy-kv atr-kv">
        {figures.map(([label, n, tone]) => (
          <div key={label}>
            <span className="tdy-kv__k">{label}</span>
            <span className={`tdy-kv__v ${tone}`}>{n}</span>
          </div>
        ))}
        {t.live && (
          <div
            title={`Its own paper account today: ${t.live.positions} ${t.live.positions === 1 ? 'position' : 'positions'} in its manual book, net after the charges of today's fills; open ones at their marks. Nothing reaches a broker.`}
          >
            <span className="tdy-kv__k">Live net</span>
            <span className={`tdy-kv__v ${netTone(t.live.net)}`}>{t.live.net == null ? '—' : formatInrSigned(t.live.net)}</span>
            <span className="tdy-kv__s faint">
              {t.live.positions} {t.live.positions === 1 ? 'trade' : 'trades'} · {t.live.open} open
            </span>
          </div>
        )}
        {t.shadowPositions > 0 && (
          <div
            title={`Its shadow book today: ${t.shadowPositions} ${t.shadowPositions === 1 ? 'buy' : 'buys'} kept by code as if placed, net after charges; open ones as if sold at their marks. Nothing reaches a broker.`}
          >
            <span className="tdy-kv__k">Shadow net</span>
            <span className={`tdy-kv__v ${netTone(t.shadowNet)}`}>{formatInrSigned(t.shadowNet)}</span>
            <span className="tdy-kv__s faint">{t.shadowOpen} open</span>
          </div>
        )}
      </div>
      {(t.mode === 'live' || t.validatorRejected + t.validatorUnavailable + t.riskMemoTightened + t.placed > 0) && (
        <p className="small-note" title="Each buy or strategy start is put to the Validator (two REJECTs, or no verdict, and it is not placed) and the Risk Memo, which may only tighten it.">
          Placed {t.placed} · validator rejected {t.validatorRejected}
          {t.validatorUnavailable > 0 ? `, gave no verdict on ${t.validatorUnavailable}` : ''} · risk memo tightened {t.riskMemoTightened}
        </p>
      )}
      {t.latest.length === 0 ? (
        <p className="tdy-none">
          No look yet today{t.status === 'on' ? '; it looks every 10 minutes from 09:20 IST on trading days' : ''}.
        </p>
      ) : (
        <ol className="tdy-dec" aria-label="The AI Trader's latest decisions">
          {t.latest.map((d, i) => (
            <li key={`${d.atUtc ?? ''}-${i}`} className="tdy-dec__row">
              <span className="tdy-dec__date" title={fullIst(d.atUtc)}>
                {istWhen(d.atUtc, now) || '—'}
              </span>
              <div className="tdy-dec__body">
                <div className="tdy-dec__title">{actionText(d)}</div>
                {d.reason && <p className="tdy-dec__text">{d.reason}</p>}
                {d.placed && (
                  <p className="tdy-dec__text">
                    {d.placed.link ? <Link to={d.placed.link}>{d.placed.text}</Link> : d.placed.text}
                  </p>
                )}
              </div>
              <div className="tdy-dec__meta">
                <AiTraderVerdict d={d} />
              </div>
            </li>
          ))}
        </ol>
      )}
    </Panel>
  )
}

// ---------- 4. the AI agents --------------------------------------------------------------------

function AgentCard({ a, now }: { a: TodayAgent; now: number }) {
  const r = a.reportsToday
  const ago = agoText(a.lastActivityUtc, now)
  return (
    <article className={`tdy-agent${a.on === false ? ' tdy-agent--off' : ''}`}>
      <header className="tdy-agent__head">
        <b className="tdy-agent__name">{a.name}</b>
        <StatusPill status={a.on === true ? 'on' : a.on === false ? 'off' : 'not known'} />
      </header>
      <p className="tdy-agent__line">
        {plural(a.callsToday, 'call')} today
        {a.failedCallsToday > 0 && (
          <>
            {' '}
            · <span className="neg">{a.failedCallsToday} failed</span>
          </>
        )}
      </p>
      <p className="tdy-agent__line">
        Reports: {r.ok} ok · <span className={r.invalid > 0 ? 'warn' : ''}>{r.invalid} invalid</span> ·{' '}
        <span className={r.failed > 0 ? 'neg' : ''}>{r.failed} failed</span>
      </p>
      <p className="tdy-agent__line faint">
        {ago ? (
          <>
            Last active <span title={fullIst(a.lastActivityUtc)}>{ago}</span>
          </>
        ) : (
          'No activity recorded'
        )}
      </p>
      {a.highlights.length > 0 && (
        <ul className="tdy-agent__hl">
          {a.highlights.map((h, i) => (
            <li key={i}>{h.link ? <Link to={h.link}>{h.text}</Link> : h.text}</li>
          ))}
        </ul>
      )}
    </article>
  )
}

function Agents({ agents, now }: { agents: TodayAgent[] | null; now: number }) {
  const on = agents?.filter((a) => a.on === true).length ?? 0
  return (
    <Panel
      title={
        <>
          <IconChip /> AI agents
          {agents && agents.length > 0 && (
            <span className="faint ai-title-note">
              {agents.length} built · {on} on
            </span>
          )}
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/agents">
          Agents →
        </Link>
      }
    >
      {agents === null ? (
        <Missing what="The agents" />
      ) : agents.length === 0 ? (
        <EmptyState>No agent is built yet.</EmptyState>
      ) : (
        <div className="tdy-agents">
          {agents.map((a) => (
            <AgentCard key={a.key} a={a} now={now} />
          ))}
        </div>
      )}
    </Panel>
  )
}

/** The overview shows this many agents with a problem; the AI tab has every card. */
const AGENTS_SHOWN = 5

/**
 * The agents on the overview: the day's totals, then only the agents that
 * had a failed call or a bad report (a quiet agent needs no line).
 */
function AgentsBrief({ agents, now, go }: { agents: TodayAgent[] | null; now: number; go: (t: TodayTab) => void }) {
  const totals = agents ? agentTotals(agents) : null
  const trouble = agents ? agents.filter(agentTrouble) : []
  return (
    <Panel
      className="tdy-agents-brief"
      title={
        <>
          <IconChip /> AI agents
          {totals && totals.built > 0 && (
            <span className="faint ai-title-note">
              {totals.on} of {totals.built} on
            </span>
          )}
        </>
      }
      actions={
        <button type="button" className="btn btn--sm btn--ghost" onClick={() => go('ai')}>
          All agents →
        </button>
      }
    >
      {agents === null ? (
        <Missing what="The agents" />
      ) : agents.length === 0 ? (
        <p className="tdy-none">No agent is built yet.</p>
      ) : (
        <>
          <dl className="tdy-mini">
            <div>
              <dt>Calls today</dt>
              <dd className="tdy-n">{totals!.calls}</dd>
            </div>
            <div>
              <dt>Failed calls</dt>
              <dd className={`tdy-n ${totals!.failedCalls > 0 ? 'neg' : ''}`}>{totals!.failedCalls}</dd>
            </div>
            <div>
              <dt>Bad reports</dt>
              <dd className={`tdy-n ${totals!.badReports > 0 ? 'warn' : ''}`} title="Reports that failed or came back invalid today">
                {totals!.badReports}
              </dd>
            </div>
          </dl>
          {trouble.length === 0 ? (
            <p className="tdy-none">No failed call or bad report today.</p>
          ) : (
            <ul className="tdy-trouble" aria-label="Agents with a failed call or a bad report today">
              {trouble.slice(0, AGENTS_SHOWN).map((a) => {
                const bad = a.reportsToday.failed + a.reportsToday.invalid
                return (
                  <li key={a.key} title={a.lastActivityUtc ? `Last active ${agoText(a.lastActivityUtc, now)}` : undefined}>
                    <span className="tdy-trouble__name">{a.name}</span>
                    <span className="tdy-trouble__what">
                      {a.failedCallsToday > 0 && <span className="neg">{plural(a.failedCallsToday, 'failed call')}</span>}
                      {a.failedCallsToday > 0 && bad > 0 && ' · '}
                      {bad > 0 && <span className="warn">{plural(bad, 'bad report')}</span>}
                    </span>
                  </li>
                )
              })}
              {trouble.length > AGENTS_SHOWN && (
                <li className="faint">and {plural(trouble.length - AGENTS_SHOWN, 'more agent')}: see AI</li>
              )}
            </ul>
          )}
        </>
      )}
    </Panel>
  )
}

// ---------- 5. learning -----------------------------------------------------------------------------

/** "11 of 12, 92%", and under the pass mark it says so: the colour is never the only sign. */
function Score({ s }: { s: CheckScore }) {
  if (s.total <= 0) return <span className="faint">no questions</span>
  const passes = s.passed / s.total >= CHECK_PASS_MARK
  return (
    <>
      <span className={passes ? 'pos' : 'warn'}>{checkScoreText({ ...s, score: null })}</span>
      {!passes && <span className="tdy-kv__s warn"> below the {Math.round(CHECK_PASS_MARK * 100)}% pass mark</span>}
    </>
  )
}

/** The check's last days with one, as small bars with their numbers; the dashed line is the pass mark. */
function CheckStrip({ days }: { days: CheckDay[] }) {
  if (days.length === 0) return <p className="tdy-none">No check has run in the last days.</p>
  const mark = `${CHECK_PASS_MARK * 100}%`
  return (
    <figure className="tdy-days">
      <ol>
        {days.map((d) => {
          const ratio = d.total > 0 ? d.passed / d.total : 0
          const passes = ratio >= CHECK_PASS_MARK
          return (
            <li key={d.date} title={`${shortDate(d.date)}: ${d.passed} of ${d.total}${passes ? '' : ', below the pass mark'}`}>
              <span className="tdy-days__n">
                {d.passed}/{d.total}
              </span>
              <span className="tdy-days__track" aria-hidden="true">
                <span className={`tdy-days__bar${passes ? '' : ' tdy-days__bar--low'}`} style={{ height: `${Math.round(ratio * 100)}%` }} />
                <i className="tdy-days__mark" style={{ bottom: mark }} />
              </span>
              <span className="tdy-days__d">{shortDate(d.date)}</span>
            </li>
          )
        })}
      </ol>
      <figcaption className="faint">
        The daily check, its last {plural(days.length, 'day')} with one; the dashed line is the {Math.round(CHECK_PASS_MARK * 100)}% pass
        mark.
      </figcaption>
    </figure>
  )
}

function Learning({ l }: { l: TodayLearning | null }) {
  return (
    <Panel
      title={
        <>
          <IconLayers /> Learning
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/memory">
          Memory →
        </Link>
      }
    >
      {l === null ? (
        <Missing what="What the agents learned" />
      ) : (
        <>
          <div className="tdy-kv">
            <div>
              <span className="tdy-kv__k">Active memories</span>
              <span className="tdy-kv__v">{l.activeMemories ?? <span className="faint">—</span>}</span>
            </div>
            <div>
              <span className="tdy-kv__k">The check today</span>
              <span className="tdy-kv__v">
                {l.checkToday ? <Score s={l.checkToday} /> : <span className="tdy-kv__s faint">not run yet today</span>}
              </span>
            </div>
          </div>
          <CheckStrip days={l.checkDays} />
          <p className="tdy-exam">
            <span className="tdy-kv__k">The weekly exam</span>{' '}
            {l.latestExam ? (
              <Link to={`/ai/reports?id=${l.latestExam.reportId}`}>
                {examText(l.latestExam)}
                {l.latestExam.date ? ` · ${l.latestExam.date}` : ''}
              </Link>
            ) : (
              <span className="faint">none finished yet; it runs on Sundays from 10:30 IST</span>
            )}
          </p>

          <h3 className="tdy-sub">Learned today</h3>
          {l.learnedToday.length === 0 ? (
            <p className="tdy-none">Nothing new today.</p>
          ) : (
            <ul className="tdy-mems">
              {l.learnedToday.map((m) => (
                <li key={m.id}>
                  <Link className="mono tdy-mems__id" to={memoryHref(m.id)}>
                    {memoryLabel(m.id)}
                  </Link>
                  <span className="tdy-mems__text">{m.text}</span>
                  <span className="tdy-mems__why">{[m.agentName, m.how].filter(Boolean).join(' · ')}</span>
                </li>
              ))}
            </ul>
          )}

          <h3 className="tdy-sub">Dropped today</h3>
          {l.droppedToday.length === 0 ? (
            <p className="tdy-none">Nothing dropped today.</p>
          ) : (
            <ul className="tdy-mems">
              {l.droppedToday.map((m) => (
                <li key={m.id}>
                  <Link className="mono tdy-mems__id" to={memoryHref(m.id)}>
                    {memoryLabel(m.id)}
                  </Link>
                  <span className="tdy-mems__text">{m.text}</span>
                  <span className="tdy-mems__why">{[m.agentName, m.why && `why: ${m.why}`].filter(Boolean).join(' · ')}</span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </Panel>
  )
}

// ---------- 6. the system ------------------------------------------------------------------------------

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

function System({ s, now }: { s: TodaySystem | null; now: number }) {
  if (s === null) {
    return (
      <Panel
        title={
          <>
            <IconServer /> System
          </>
        }
      >
        <Missing what="The system's state" />
      </Panel>
    )
  }
  const sentinel = sentinelState(s.sentinelLastUtc, now)
  const verdict = s.checkup ? verdictBadge({ status: 'done', verdict: s.checkup.verdict }) : null
  const summary = s.deploy ? deploySummary(s.deploy.summary) : ''
  return (
    <Panel
      title={
        <>
          <IconServer /> System
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/system">
          Health →
        </Link>
      }
    >
      <dl className="tdy-facts">
        <Fact label="Sentinel">
          {sentinel.late && <IconWarning className="warn" aria-hidden="true" />}
          <span className={sentinel.late ? 'warn' : ''} title={fullIst(s.sentinelLastUtc)}>
            {sentinel.text}
          </span>
        </Fact>
        <Fact label="Checkup">
          {s.checkup && verdict ? (
            <>
              <Badge tone={verdict.tone}>{verdict.label}</Badge>
              {s.checkup.headline && <span>{s.checkup.headline}</span>}
              {s.checkup.utc && (
                <span className="faint" title={fullIst(s.checkup.utc)}>
                  {istWhen(s.checkup.utc, now)}
                </span>
              )}
              <Link className="tdy-open" to="/system/checkups">
                Open →
              </Link>
            </>
          ) : (
            <span className="faint">No checkup yet.</span>
          )}
        </Fact>
        <Fact label="Last deploy">
          {s.deploy ? (
            <>
              {s.deploy.commit && <span className="mono">{s.deploy.commit}</span>}
              {s.deploy.utc && (
                <span className="faint" title={fullIst(s.deploy.utc)}>
                  {istWhen(s.deploy.utc, now)} · {agoText(s.deploy.utc, now)}
                </span>
              )}
              {summary && <span className="tdy-facts__wide">{summary}</span>}
            </>
          ) : (
            <span className="faint">No deploy recorded.</span>
          )}
        </Fact>
        <Fact label="Incidents">
          {s.openIncidents == null ? (
            <span className="faint">not known</span>
          ) : s.openIncidents === 0 ? (
            <span>None open</span>
          ) : (
            <span className="warn">{s.openIncidents} open</span>
          )}
          <Link className="tdy-open" to="/system/incidents">
            Incidents →
          </Link>
        </Fact>
      </dl>
    </Panel>
  )
}

// ---------- 7. decisions -----------------------------------------------------------------------------

/** The list is read in half a minute: the newest few in full, the rest folded. */
const DECISIONS_SHOWN = 8

function DecisionRow({ d }: { d: TodayDecision }) {
  const st = decisionStatus(d.status)
  return (
    <li className="tdy-dec__row">
      <span className="tdy-dec__date" title={d.date}>
        {d.date ? shortDate(d.date) : '—'}
      </span>
      <div className="tdy-dec__body">
        <div className="tdy-dec__title">{d.title}</div>
        {d.decided && <p className="tdy-dec__text">{d.decided}</p>}
      </div>
      <div className="tdy-dec__meta">
        <span title={st.means || undefined}>
          <Badge tone={st.tone}>{st.label}</Badge>
        </span>
        {d.by && <span>by {d.by}</span>}
      </div>
    </li>
  )
}

function Decisions({ list }: { list: TodayDecision[] | null }) {
  const shown = list?.slice(0, DECISIONS_SHOWN) ?? []
  const rest = list?.slice(DECISIONS_SHOWN) ?? []
  return (
    <Panel
      title={
        <>
          <IconPen /> Decisions <span className="faint ai-title-note">newest first</span>
        </>
      }
    >
      {list === null ? (
        <Missing what="The decisions" />
      ) : list.length === 0 ? (
        <EmptyState>No decisions recorded.</EmptyState>
      ) : (
        <>
          <ol className="tdy-dec">
            {shown.map((d, i) => (
              <DecisionRow key={`${d.date}-${d.title}-${i}`} d={d} />
            ))}
          </ol>
          {rest.length > 0 && (
            <details className="hp-details tdy-more">
              <summary>{plural(rest.length, 'earlier decision')}</summary>
              <ol className="tdy-dec">
                {rest.map((d, i) => (
                  <DecisionRow key={`${d.date}-${d.title}-${i}`} d={d} />
                ))}
              </ol>
            </details>
          )}
        </>
      )}
    </Panel>
  )
}

// ---------- the page ------------------------------------------------------------------------------------

/** Why the page could not be read, in words the owner can act on. */
function failureText(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403) return "Today is the admin's page, and this account may not read it."
    if (error.status === 404) return 'This server does not have the Today page yet: deploy the current API build.'
  }
  return `Today could not be read: ${errorText(error)}`
}

export function TodayPage() {
  const today = useToday()
  // Ages ("4 min ago") and Sentinel's freshness move between reads.
  const now = useNow(15_000)
  const d = today.data
  const [params, setParams] = useSearchParams()
  const tab = todayTab(params.get('tab'))
  const go = (key: TodayTab) => {
    setParams(key === 'overview' ? {} : { tab: key }, { replace: true })
    window.scrollTo({ top: 0 })
  }
  const counts: Partial<Record<TodayTab, number | null>> = {
    trading: d?.trading ? d.trading.runs.length : null,
    ai: d?.agents ? d.agents.length : null,
    decisions: d?.decisions ? d.decisions.length : null,
  }

  return (
    <div className="page hp tdy">
      <Header d={d} updatedAt={today.dataUpdatedAt} refreshFailed={today.isError} now={now} />

      {today.isPending ? (
        <Loading label="Reading today…" />
      ) : !d ? (
        <div className="stack-list">
          <InlineError error={new Error(failureText(today.error))} />
          <p className="small-note">
            Everything on this page comes from one request, GET /api/Today, read again every 30 s. Nothing here is known until it answers;
            the other pages still work.
          </p>
        </div>
      ) : (
        <>
          <div className="oc-tabs tdy-tabs" role="tablist" aria-label="Today">
            {TODAY_TABS.map((t) => (
              <button
                key={t.key}
                type="button"
                role="tab"
                id={`tdy-tab-${t.key}`}
                aria-selected={tab === t.key}
                aria-controls="tdy-tabpanel"
                className={`oc-tab ${tab === t.key ? 'oc-tab--on' : ''}`}
                onClick={() => go(t.key)}
              >
                {t.label}
                {counts[t.key] != null && <span className="tdy-tab__n">{counts[t.key]}</span>}
              </button>
            ))}
          </div>

          <div role="tabpanel" id="tdy-tabpanel" aria-labelledby={`tdy-tab-${tab}`} className="tdy-body">
            {tab === 'overview' && (
              <>
                <Kpis d={d} now={now} go={go} />
                <div className="tdy-grid tdy-grid--main">
                  <Trading t={d.trading} now={now} brief go={go} />
                  <Attention items={d.attention} now={now} />
                </div>
                <div className="tdy-grid tdy-grid--three">
                  <Markets now={now} />
                  <AgentsBrief agents={d.agents} now={now} go={go} />
                  <System s={d.system} now={now} />
                </div>
              </>
            )}
            {tab === 'trading' && <Trading t={d.trading} now={now} />}
            {tab === 'ai' && (
              <>
                {d.aiTrader ? (
                  <AiTrader t={d.aiTrader} now={now} />
                ) : (
                  <p className="small-note">The AI Trader has not been switched on and has not looked at the market today.</p>
                )}
                <Agents agents={d.agents} now={now} />
                <Learning l={d.learning} />
              </>
            )}
            {tab === 'decisions' && <Decisions list={d.decisions} />}
          </div>
        </>
      )}
    </div>
  )
}
