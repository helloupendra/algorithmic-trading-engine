/**
 * The status strip: the Desk in one line. What it holds follows the part of
 * the day: readiness before the open (checkup, token, feeds, plan, forecasts,
 * GIFT, what was carried in), then net P&L per account against the platform
 * limit with runs, legs, the feed, incidents and the checkup, then the day's
 * result. A trader's strip holds only their own numbers and the market; no
 * connector, feed or Sentinel cell is ever built for them.
 *
 * Each cell asks for what it shows and says "…" until it knows. On a phone
 * the strip packs into rows (lib/desk.ts, stripSpans).
 */

import type { CSSProperties, ReactNode } from 'react'
import { allows } from '../../lib/modules'
import {
  GIFT_KEY,
  byUnderlying,
  dayOf,
  forecastCounts,
  istHm,
  latestSnapshots,
  modelStanding,
  plainNumber,
  runCounts,
  stripSpans,
  underlyingShort,
  untilText,
  weekdayOf,
} from '../../lib/desk'
import { statusMeta } from '../../lib/analysis'
import { verdictBadge, slotLabel } from '../../lib/checkup'
import { formatInrWhole } from '../../lib/format'
import { connectorsSummary, feedPulses } from '../../lib/pulse'
import { silenceNote } from '../../lib/incidents'
import {
  useCheckupLatest,
  useFeeds,
  useForecastScoreboard,
  useIncidentSummary,
  useIngestorStatuses,
  useIntelSnapshots,
  useKillSwitch,
  useMarketPulse,
  useProviders,
} from '../../lib/queries'
import type { DeskView } from './data'
import { toneClass, useDayForecasts, useDeskLegs, useDeskPlan } from './data'
import { Dot, Money, Swatch } from './parts'

type Kind = 'lead' | 'account' | 'other'
type Tone = 'pos' | 'neg' | 'warn' | 'live' | null

/** One cell: a label, a value (or "…" until it is known), a line under it, and sometimes a rail. */
function Cell({ label, value, sub, lead, rail, tone, title }: {
  label: ReactNode
  value: ReactNode | null
  sub?: ReactNode
  lead?: boolean
  rail?: ReactNode
  tone?: Tone
  title?: string
}) {
  return (
    <>
      <div className="dk-v__l">{label}</div>
      <div className={`dk-v__val${lead ? ' dk-v__val--lead' : ''}${tone && tone !== 'live' ? ` ${tone}` : ''}`} title={title}>
        {value ?? <span className="dk-t3">…</span>}
      </div>
      {sub && <div className="dk-v__s">{sub}</div>}
      {rail && <div className="dk-v__rail">{rail}</div>}
    </>
  )
}


// ---------------------------------------------------------------- run cells

function NetLead({ view }: { view: DeskView }) {
  const all = view.scope === 'all' && view.allAccounts.length > 1
  const who = !view.isAdmin ? 'My' : null
  const day = view.phase === 'post' ? 'result' : 'net today'
  const whose = all ? 'all accounts' : (view.scopeName ?? view.accounts[0]?.name)
  const label = who ? `${who} ${day}` : `${view.phase === 'post' ? 'Day result' : 'Net today'}${whose ? ` · ${whose}` : ''}`
  const f = view.grid?.figures
  return (
    <Cell
      lead
      label={label}
      value={view.grid ? <Money value={f!.net} /> : null}
      sub={f ? `after ${formatInrWhole(f.charges)} charges` : undefined}
    />
  )
}

// No loss rail here: the only loss limit is per run (Risk → max daily loss,
// checked on each new order), so an account's total measured against it read
// as "144% of max daily loss" on a day no run was near it.
function AccountCell({ view, index }: { view: DeskView; index: number }) {
  const totals = view.grid?.totals[index]
  const account = view.accounts[index]
  return (
    <Cell
      label={
        <>
          <Swatch tone={account.tone} />
          {account.name}
        </>
      }
      value={totals ? <Money value={totals.figures.net} /> : null}
      sub={totals ? `${totals.runs} runs · ${formatInrWhole(totals.figures.charges)} charges` : undefined}
    />
  )
}

function RunsCell({ view }: { view: DeskView }) {
  const counts = view.runs ? runCounts(view.runs) : null
  if (!counts) return <Cell label={view.isAdmin ? 'Runs' : 'My runs'} value={null} />
  if (view.phase === 'pre') {
    return (
      <Cell
        label={view.isAdmin ? 'Runs' : 'My runs'}
        value={`${counts.live} deployed`}
        sub={counts.total > counts.live ? `${counts.total - counts.live} stopped` : 'waiting for the open'}
      />
    )
  }
  if (view.phase === 'post') {
    const extra = [counts.rule ? `${counts.rule} stopped (SL)` : '', counts.fault ? `${counts.fault} stopped by a fault` : '', counts.live ? `${counts.live} still live` : '']
      .filter(Boolean)
      .join(' · ')
    return <Cell label={view.isAdmin ? 'Runs' : 'My runs'} value={`${counts.closed} closed`} sub={extra || `${counts.total} today`} />
  }
  return <RunsLiveCell view={view} counts={counts} />
}

function RunsLiveCell({ view, counts }: { view: DeskView; counts: ReturnType<typeof runCounts> }) {
  const stopped = [
    counts.rule ? <span key="r" className="neg">{counts.rule} stopped (SL)</span> : null,
    counts.fault ? <span key="f" className="warn">{counts.fault} stopped by a fault</span> : null,
    counts.target ? <span key="t">{counts.target} at target</span> : null,
  ].filter(Boolean)
  const sub = (
    <>
      {stopped.map((s, i) => (
        <span key={i}>
          {i > 0 && ' · '}
          {s}
        </span>
      ))}
      {view.isAdmin && <PlannedSuffix view={view} lead={stopped.length > 0} />}
      {!view.isAdmin && !stopped.length && `of ${counts.total} today`}
    </>
  )
  return <Cell label={view.isAdmin ? 'Runs' : 'My runs'} value={`${counts.live} live`} sub={sub} />
}

/** "· 23 planned", from the morning plan file (admin). */
function PlannedSuffix({ view, lead }: { view: DeskView; lead: boolean }) {
  const { plan } = useDeskPlan(view)
  if (!plan) return null
  return <>{`${lead ? ' · ' : ''}${plan.planned} planned`}</>
}

/** The plan's runs live against the runs it asks for, per account; the API's own test of live (runner alive). */
function PlanCell({ view }: { view: DeskView }) {
  const { plan, ready, missing } = useDeskPlan(view)
  const first = view.runs?.map((r) => r.startedUtc).filter(Boolean).sort()[0] ?? null
  if (!ready) return <Cell label="Morning plan" value={null} />
  if (missing) return <Cell label="Morning plan" value="no plan file" tone="warn" sub="the morning job starts nothing without one" />
  if (!plan) return <Cell label="Morning plan" value="?" sub="could not read the plan" />
  const perAccount = plan.accounts.length > 1 ? plan.accounts.map((a) => `${a.name} ${a.live}/${a.planned}`).join(' · ') : ''
  const warned = plan.warnings.length ? `${plan.warnings.length} warning${plan.warnings.length === 1 ? '' : 's'}` : ''
  return (
    <Cell
      label="Morning plan"
      value={`${plan.live} / ${plan.planned} live`}
      tone={plan.live >= plan.planned && !warned ? 'pos' : 'warn'}
      title={plan.warnings.join('\n') || undefined}
      sub={[warned, first ? `deployed ${istHm(first)}` : 'not deployed yet', perAccount].filter(Boolean).join(' · ')}
    />
  )
}

function LegsCell({ view }: { view: DeskView }) {
  const { legs } = useDeskLegs(view)
  if (!legs) return <Cell label={view.phase === 'post' ? 'Held overnight' : 'Open legs'} value={null} />
  const carried = legs.filter((l) => l.carriedFrom)
  if (view.phase === 'pre') {
    const pnl = carried.reduce((a, l) => a + (l.pnl ?? 0), 0)
    const where = [...new Set(carried.map((l) => (l.manual ? `${l.userName} book` : l.userName)))].join(', ')
    return (
      <Cell
        label="Carried in"
        value={carried.length ? `${carried.length} leg${carried.length === 1 ? '' : 's'}` : 'none'}
        sub={carried.length ? <><Money value={pnl} /> · {where}</> : 'nothing held overnight'}
        title={carried.map((l) => `${l.label} · ${l.userName}`).join(', ')}
      />
    )
  }
  if (view.phase === 'post') {
    const expiring = legs.filter((l) => l.expires)
    return (
      <Cell
        label="Held overnight"
        value={legs.length ? `${legs.length} leg${legs.length === 1 ? '' : 's'}` : 'none'}
        sub={
          expiring.length ? (
            <span className="warn">
              {expiring.length} expire{expiring.length === 1 ? 's' : ''} {expiring[0].expires === 'today' || !expiring[0].expiryDate ? 'today' : weekdayOf(expiring[0].expiryDate)}
            </span>
          ) : legs.length ? (
            'none expiring next session'
          ) : (
            'flat overnight'
          )
        }
      />
    )
  }
  const from = [...new Set(carried.map((l) => l.carriedFrom))].join(', ')
  return (
    <Cell
      label="Open legs"
      value={`${legs.length}`}
      sub={carried.length ? `${carried.length} carried from ${from}` : [...new Set(legs.map((l) => l.underlying))].sort(byUnderlying).map(underlyingShort).join(', ') || 'flat'}
    />
  )
}

function ChargesCell({ view }: { view: DeskView }) {
  const f = view.grid?.figures
  return (
    <Cell
      label="Charges today"
      value={f ? formatInrWhole(f.charges) : null}
      sub={f ? <>{`${f.trades} trades · gross `}<Money value={f.gross} /></> : undefined}
    />
  )
}

// ---------------------------------------------------------------- operator cells (admin)

function FeedCell({ view }: { view: DeskView }) {
  const feeds = useFeeds()
  const beats = useIngestorStatuses()
  const providers = useProviders()
  if (!feeds.data) return <Cell label="Feed" value={null} />
  const nseOpen = view.nse?.isMarketOpen === true
  const [first] = feedPulses(feeds.data, beats.data, nseOpen, view.nowMs)
  const summary = connectorsSummary(providers.data, feeds.data, view.nse?.isTradingDay !== false, view.nowMs)
  const backups = summary?.backupsDown.map((l) => `${l.name} backup signed out`).join(' · ')
  if (!first) return <Cell label="Feed" value="none running" sub={backups || 'no feed on'} />
  return (
    <Cell
      label="Feed"
      tone={first.tone === 'live' ? 'live' : first.tone === 'neg' ? 'neg' : first.tone === 'warn' ? 'warn' : null}
      title={first.title}
      value={
        <>
          <Dot tone={first.tone === 'live' ? 'live' : first.tone === 'neg' ? 'neg' : first.tone === 'warn' ? 'warn' : null} />
          {first.label}
        </>
      }
      sub={backups || first.title}
    />
  )
}

function DhanCell() {
  const providers = useProviders()
  if (!providers.data) return <Cell label="Dhan token" value={null} />
  const dhan = providers.data.find((p) => p.key === 'dhan')
  if (!dhan || !dhan.isConfigured) return <Cell label="Dhan token" value="not set up" />
  const s = dhan.session
  if (!s.isConnected || s.needsReconnect) {
    return <Cell label="Dhan token" value="Signed out" tone="neg" sub={s.expiresUtc ? `ended ${istHm(s.expiresUtc)}` : 'sign in before the open'} />
  }
  const expires = s.expiresUtc ? Date.parse(s.expiresUtc) : null
  const valid = expires == null ? '' : `until ${istHm(s.expiresUtc)}`
  return (
    <Cell
      label="Dhan token"
      value="Signed in"
      tone="pos"
      title={s.expiresUtc ? `Valid until ${new Date(s.expiresUtc).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })}` : undefined}
      sub={[s.connectedUtc ? `since ${istHm(s.connectedUtc)}` : '', valid].filter(Boolean).join(' · ')}
    />
  )
}

function IncidentsCell({ view }: { view: DeskView }) {
  const summary = useIncidentSummary()
  const s = summary.data
  if (!s) return <Cell label="Incidents" value={summary.isError ? '?' : null} sub={summary.isError ? 'could not read Sentinel' : undefined} />
  const quiet = silenceNote(s.lastCheckUtc, summary.dataUpdatedAt || view.nowMs)
  if (quiet) return <Cell label="Incidents" value="Sentinel quiet" tone="warn" title={quiet} sub="a zero from it proves nothing" />
  const live = typeof s.live === 'number' ? s.live : Object.values(s.counts ?? {}).reduce((a, b) => a + (b ?? 0), 0)
  const by = (['critical', 'high', 'medium', 'low'] as const).flatMap((k) => Array(s.counts?.[k] ?? 0).fill(k)).join(' · ')
  const tone: Tone = live === 0 ? null : s.worstSeverity === 'critical' || s.worstSeverity === 'high' ? 'neg' : 'warn'
  return <Cell label="Incidents" value={`${live} open`} tone={tone} sub={live ? by : 'none live'} />
}

function CheckupCell({ lead = false }: { lead?: boolean }) {
  const latest = useCheckupLatest()
  const c = latest.data?.latest
  if (!latest.data) return <Cell lead={lead} label={lead ? 'Desk' : 'Checkup'} value={null} />
  if (!c) return <Cell lead={lead} label={lead ? 'Desk' : 'Checkup'} value="no checkup yet" />
  const badge = verdictBadge(c)
  const tone: Tone = badge.tone === 'pos' ? 'pos' : badge.tone === 'neg' ? 'neg' : badge.tone === 'warn' ? 'warn' : null
  return (
    <Cell
      lead={lead}
      label={lead ? 'Desk' : 'Checkup'}
      value={badge.label}
      tone={tone}
      sub={lead ? `${istHm(c.completedUtc)} · ${c.headline}` : `${slotLabel(c.slot).toLowerCase()} · ${istHm(c.completedUtc)}`}
    />
  )
}

function GiftCell({ view }: { view: DeskView }) {
  const snaps = useIntelSnapshots(view.today, true)
  if (!snaps.data) return <Cell label="GIFT Nifty" value={null} />
  const gift = latestSnapshots(snaps.data).get(GIFT_KEY)
  if (!gift) return <Cell label="GIFT Nifty" value="no snapshot yet" sub="the recorder starts at 06:00" />
  return (
    <Cell
      label="GIFT Nifty"
      value={plainNumber(gift.price, 1)}
      sub={
        <>
          {gift.changePct != null && <span className={toneClass(gift.changePct)}>{`${gift.changePct > 0 ? '+' : gift.changePct < 0 ? '−' : ''}${Math.abs(gift.changePct).toFixed(2)}%`}</span>}
          {` · ${istHm(gift.asOfUtc ?? gift.fetchedUtc)}`}
        </>
      }
    />
  )
}

// ---------------------------------------------------------------- the market, forecasts, the clock

function ForecastCell({ view }: { view: DeskView }) {
  const list = useDayForecasts(view)
  const board = useForecastScoreboard()
  if (!list.data) return <Cell label="Forecasts" value={list.isError ? '?' : null} />
  const c = forecastCounts(list.data, view.day)
  const standing = modelStanding(board.data)
  const status = standing ? statusMeta(standing.status).label.toLowerCase() : 'not proven'
  if (view.phase === 'post' && c.rangesScored) {
    return (
      <Cell
        label="Forecasts"
        value={`${c.rangesInside} / ${c.rangesScored} in band`}
        tone={c.rangesInside === c.rangesScored ? 'pos' : null}
        sub={`scored ${istHm(list.data.map((f) => f.scoredUtc).filter(Boolean).sort().at(-1) ?? null)} · ${status}`}
      />
    )
  }
  if (!c.issued) return <Cell label="Forecasts" value="none yet" sub="issued at 08:50 on trading days" />
  return <Cell label="Forecasts" value={`${c.issued} issued`} sub={`${istHm(c.issuedUtc)} · ${status}`} />
}

function NiftyCell() {
  const pulse = useMarketPulse()
  const nifty = pulse.data?.groups.find((g) => g.key === 'index')?.items.find((i) => i.symbol === 'NSE:NIFTY50-INDEX')
  if (!nifty || nifty.lastTradedPrice == null) return <Cell label="NIFTY 50" value={null} />
  const range = nifty.high != null && nifty.low != null ? ` · range ${Math.round(nifty.high - nifty.low)} pts` : ''
  return (
    <Cell
      label="NIFTY 50"
      value={plainNumber(nifty.lastTradedPrice)}
      sub={
        <>
          {nifty.changePercent != null && <span className={toneClass(nifty.changePercent)}>{`${nifty.changePercent > 0 ? '+' : nifty.changePercent < 0 ? '−' : ''}${Math.abs(nifty.changePercent).toFixed(2)}%`}</span>}
          {range}
        </>
      }
    />
  )
}

/** Whether the prices are fresh: the age of the data itself, which is all a trader needs of the feed. */
function PricesCell({ view }: { view: DeskView }) {
  const pulse = useMarketPulse()
  const at = pulse.data?.latestQuoteUtc
  if (!pulse.data) return <Cell label="Prices" value={null} />
  if (!at) return <Cell label="Prices" value="none yet" />
  const age = Math.max(0, view.nowMs - Date.parse(at))
  const fresh = age < 5 * 60_000
  const inSession = view.clock === 'live'
  return (
    <Cell
      label="Prices"
      value={fresh ? 'fresh' : inSession ? 'stale' : `from ${istHm(at)}`}
      tone={fresh ? 'pos' : inSession ? 'warn' : null}
      sub={fresh ? `last quote ${Math.round(age / 1000)} s ago` : `last quote ${weekdayOf(dayOf(at)!)} ${istHm(at)}`}
    />
  )
}

function TradingCell() {
  const ks = useKillSwitch()
  if (!ks.data) return <Cell label="Trading" value={null} />
  return ks.data.isActive ? (
    <Cell label="Trading" value="halted" tone="neg" sub={ks.data.reason ? `kill switch: ${ks.data.reason}` : 'kill switch on'} />
  ) : (
    <Cell label="Trading" value="allowed" tone="pos" sub="kill switch off" />
  )
}

function OpensCell({ view }: { view: DeskView }) {
  const nse = view.nse
  if (!nse) return <Cell label="NSE opens" value={null} />
  const open = Date.parse(nse.isTradingDay && Date.parse(nse.sessionOpenUtc) > view.nowMs ? nse.sessionOpenUtc : nse.nextMarketOpenUtc)
  const mcx = view.mcx && view.mcx.isTradingDay && Date.parse(view.mcx.sessionOpenUtc) > view.nowMs ? ` · MCX ${istHm(view.mcx.sessionOpenUtc)}` : ''
  return <Cell label="NSE opens" value={istHm(new Date(open).toISOString())} sub={`${untilText(open, view.nowMs) ?? 'now'}${mcx}`} />
}

// ---------------------------------------------------------------- the strip

interface Spec {
  key: string
  kind: Kind
  node: ReactNode
}

function specs(view: DeskView): Spec[] {
  const out: Spec[] = []
  const add = (key: string, kind: Kind, node: ReactNode) => out.push({ key, kind, node })
  const strategies = allows(view.access, 'strategies')
  const analysis = allows(view.access, 'analysis')
  const perAccount = view.scope === 'all' && view.accounts.length > 1

  if (view.phase === 'pre') {
    if (view.isAdmin) {
      add('checkup', 'lead', <CheckupCell lead />)
      add('dhan', 'other', <DhanCell />)
      add('feed', 'other', <FeedCell view={view} />)
      if (strategies) add('plan', 'other', <PlanCell view={view} />)
      if (analysis) add('fc', 'other', <ForecastCell view={view} />)
      add('gift', 'other', <GiftCell view={view} />)
      if (strategies) add('legs', 'other', <LegsCell view={view} />)
      add('opens', 'other', <OpensCell view={view} />)
      return out
    }
    add('opens', 'lead', <OpensCell view={view} />)
    if (strategies) {
      add('runs', 'other', <RunsCell view={view} />)
      add('legs', 'other', <LegsCell view={view} />)
    }
    if (analysis) add('fc', 'other', <ForecastCell view={view} />)
    add('nifty', 'other', <NiftyCell />)
    add('prices', 'other', <PricesCell view={view} />)
    add('trading', 'other', <TradingCell />)
    return out
  }

  if (!strategies) {
    add('nifty', 'lead', <NiftyCell />)
    add('prices', 'other', <PricesCell view={view} />)
    add('trading', 'other', <TradingCell />)
    return out
  }
  add('net', 'lead', <NetLead view={view} />)
  if (perAccount) view.accounts.forEach((a, i) => add(`acct-${a.id}`, 'account', <AccountCell view={view} index={i} />))
  add('runs', 'other', <RunsCell view={view} />)
  if (view.phase === 'post' && analysis) add('fc', 'other', <ForecastCell view={view} />)
  add('legs', 'other', <LegsCell view={view} />)
  if (view.isAdmin) {
    if (view.phase === 'live') add('feed', 'other', <FeedCell view={view} />)
    if (view.phase === 'post') add('checkup', 'other', <CheckupCell />)
    add('incidents', 'other', <IncidentsCell view={view} />)
    if (view.phase === 'live') add('checkup', 'other', <CheckupCell />)
  } else {
    add('charges', 'other', <ChargesCell view={view} />)
    add('nifty', 'other', <NiftyCell />)
    add('prices', 'other', <PricesCell view={view} />)
    add('trading', 'other', <TradingCell />)
  }
  return out
}

export function StatusStrip({ view }: { view: DeskView }) {
  const cells = specs(view)
  const spans = stripSpans(cells.map((c) => c.kind))
  const columns = `1.25fr repeat(${cells.length - 1}, minmax(0, 1fr))`
  return (
    <section className="dk-strip" style={{ gridTemplateColumns: columns }} aria-label="The desk in one line">
      {cells.map((c, i) => (
        <div key={c.key} className="dk-v" style={{ '--span': spans[i] } as CSSProperties}>
          {c.node}
        </div>
      ))}
    </section>
  )
}
