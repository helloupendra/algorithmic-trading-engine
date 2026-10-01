/**
 * Today (/today): the owner's one page, and an admin's home.
 *
 * The owner asked on 1 Oct for one section where he sees everything, because
 * he is busy and cannot keep answering yes/no. So this page is read-first:
 * nothing on it needs a click to keep the desk going, and it asks nothing.
 * Its links only open the page that has a thing in full.
 *
 * Top to bottom, as the owner reads it in half a minute: the date and the
 * markets; what needs a look (or one calm line when nothing does); today's
 * live trading by account and by run; the AI Trader (once it has been on);
 * the AI agents; what they learned; the system; and every decision taken for
 * or by the owner.
 *
 * One request (GET /api/Today, lib/today.ts), read again every 30 s. A part
 * the API left out says so in words; it is never drawn as an empty part.
 * Money is signed and grouped the Indian way, times are IST, and every
 * colour also says what it means in words or an icon.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { CHECK_PASS_MARK, checkScoreText, memoryHref, memoryLabel, shortDate } from '../../lib/ai'
import { actionText, modeLabel } from '../../lib/aiTrader'
import { ApiError } from '../../lib/api'
import { verdictBadge } from '../../lib/checkup'
import { istDay, strategyLabel } from '../../lib/desk'
import { deploySummary } from '../../lib/deploys'
import { formatDateTime, formatInrSigned, formatInrWhole, formatTime } from '../../lib/format'
import { shortStopReason } from '../../lib/runHistory'
import {
  TODAY_POLL_MS,
  agoText,
  attentionKind,
  attentionLevel,
  decisionStatus,
  examText,
  istWhen,
  longDate,
  marketText,
  plural,
  recapText,
  runStatus,
  sentinelState,
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
  TodayTrading,
} from '../../lib/today'
import { Badge, EmptyState, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { IconAlert, IconChip, IconInfo, IconLayers, IconPen, IconServer, IconSwitch, IconWarning } from '../../components/icons'
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

// ---------- 2. needs a look ---------------------------------------------------------------

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

function AttentionRow({ item, now }: { item: AttentionItem; now: number }) {
  const tone = attentionLevel(item.level).tone
  const kind = attentionKind(item.kind)
  const at = istWhen(item.atUtc, now)
  return (
    <li className={`tdy-att__row tdy-att__row--${tone}`}>
      <LevelMark level={item.level} />
      <div className="tdy-att__body">
        <div className="tdy-att__title">{item.title}</div>
        {item.detail && <div className="tdy-att__detail">{item.detail}</div>}
      </div>
      <div className="tdy-att__meta">
        {kind && <span>{kind}</span>}
        {at && <span title={fullIst(item.atUtc)}>{at}</span>}
        {item.link && (
          <Link className="tdy-open" to={item.link}>
            Open →
          </Link>
        )}
      </div>
    </li>
  )
}

function Attention({ items, now }: { items: AttentionItem[] | null; now: number }) {
  return (
    <Panel
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
        <ul className="tdy-att">
          {items.map((item, i) => (
            <AttentionRow key={`${item.level}-${item.title}-${i}`} item={item} now={now} />
          ))}
        </ul>
      )}
    </Panel>
  )
}

// ---------- 3. trading today ----------------------------------------------------------------

function AccountCard({ a }: { a: TodayAccount }) {
  return (
    <StatTile
      label={`${a.userName} · net today`}
      value={formatInrSigned(a.net)}
      tone={moneyTone(a.net)}
      sub={
        <>
          <span className="tdy-line">
            gross <Rupees value={a.gross} /> · charges <span className="tdy-n">{formatInrWhole(a.charges)}</span>
          </span>
          <span className="tdy-line">
            {a.runsLive} live · {a.runsStopped} stopped · {plural(a.openLegs, 'open leg')}
          </span>
        </>
      }
    />
  )
}

function RunRow({ r, now }: { r: TodayRun; now: number }) {
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
      </td>
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
    </tr>
  )
}

/**
 * The same runs as a list, for a phone: the net beside the name on the first
 * line, where a sideways-scrolling table would have hidden it past the edge.
 */
function RunList({ runs, now }: { runs: TodayRun[]; now: number }) {
  return (
    <div className="tdy-rl">
      <p className="tdy-rl__cap">Today's runs, worst net first</p>
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

function Trading({ t, now }: { t: TodayTrading | null; now: number }) {
  const recaps = t ? recapText(t.recapsToday) : ''
  return (
    <Panel
      title={
        <>
          <IconSwitch /> Trading today <span className="faint ai-title-note">live runs only</span>
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/trade/runs">
          Runs →
        </Link>
      }
    >
      {t === null ? (
        <Missing what="Today's trading" />
      ) : (
        <>
          {t.accounts.length === 0 && t.runs.length === 0 ? (
            <EmptyState>No live runs today.</EmptyState>
          ) : (
            <>
              {t.accounts.length > 0 && (
                <div className="stat-grid tdy-accounts">
                  {t.accounts.map((a) => (
                    <AccountCard key={a.userName} a={a} />
                  ))}
                </div>
              )}
              {t.runs.length > 0 && (
                <div className="tablewrap tablewrap--tall tdy-runs">
                  <table className="table">
                    <caption className="tdy-caption">Today's runs, worst net first</caption>
                    <thead>
                      <tr>
                        <th>Strategy</th>
                        <th>Index</th>
                        <th>Account</th>
                        <th>Status</th>
                        <th className="num">Trades</th>
                        <th className="num">Net</th>
                        <th>Stop reason</th>
                      </tr>
                    </thead>
                    <tbody>
                      {t.runs.map((r) => (
                        <RunRow key={r.runId} r={r} now={now} />
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              {t.runs.length > 0 && <RunList runs={t.runs} now={now} />}
            </>
          )}
          {recaps && <p className="small-note">{recaps}</p>}
        </>
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
      </div>
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
          <Attention items={d.attention} now={now} />
          <Trading t={d.trading} now={now} />
          {d.aiTrader && <AiTrader t={d.aiTrader} now={now} />}
          <Agents agents={d.agents} now={now} />
          <div className="two-col tdy-cols">
            <Learning l={d.learning} />
            <System s={d.system} now={now} />
          </div>
          <Decisions list={d.decisions} />
        </>
      )}
    </div>
  )
}
