/**
 * The AI Trader's decisions, as the pages show them: on its panel on
 * AI → Agents (a day's), on Data → Replay (a replay's) and, three at a
 * time, on Today. One row a look: the IST time, what it proposed, the
 * verdict, the model's reason and confidence. A row opens to the whole
 * decision: the verdict's why, what was placed, the plan and result JSON,
 * and the brief exactly as the model read it.
 *
 * And its shadow book, a day's or a replay's: the buys code kept as if
 * placed, each with its entry, stop and target, its mark or exit, how it
 * ended and its net after charges; and its scoreboard, each replay and live
 * shadow day against a fixed rule, with totals over the full days only.
 *
 * Read-only. The words come from lib/aiTrader.ts, so every page says a
 * decision the same way.
 */

import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import {
  actionText,
  baselineText,
  beatText,
  confidenceText,
  contractText,
  jsonBlock,
  looksSpanText,
  lotsText,
  modeLabel,
  netTone,
  placedText,
  planFacts,
  sampleNote,
  scoreKindText,
  scoreRowAnchor,
  shadowCountsText,
  shadowEnding,
  shadowLastPrice,
  shadowResultText,
  verdictOf,
} from '../../lib/aiTrader'
import type { AiTraderDecision, AiTraderDecisionFilter, AiTraderPoll, AiTraderShadowPosition } from '../../lib/aiTrader'
import { useAiTraderDecision, useAiTraderDecisions, useAiTraderPositions, useAiTraderScoreboard } from '../../lib/queries'
import { shortDay } from '../../lib/replay'
import { formatDateTime, formatInrSigned, formatInrWhole, formatPrice } from '../../lib/format'
import { EmptyState, InlineError, Loading } from '../../components/ui'
import { CallLink, ModelLabel } from './parts'
import './aiTrader.css'

/** The verdict as a badge, with what it means in its title. */
export function AiTraderVerdict({ d }: { d: { action: string; allowed: boolean; rule: string } }) {
  const v = verdictOf(d)
  return (
    <span className={`badge badge--${v.tone} atr-verdict`} title={v.means || undefined}>
      {v.label}
    </span>
  )
}

/** One decision in full, read when its row is opened. */
function DecisionDetail({ summary, domId }: { summary: AiTraderDecision; domId: string }) {
  const q = useAiTraderDecision(summary.id)
  const detail = q.data
  const d = detail?.decision ?? summary
  const placed = placedText(d)
  const mode = modeLabel(d.mode)
  const facts = planFacts(detail?.plan)
  const plan = detail ? jsonBlock(detail.planJson) : ''
  const result = detail ? jsonBlock(detail.resultJson) : ''
  const shadow = detail ? shadowResultText(detail.resultJson) : null

  return (
    <div className="atr-detail" id={domId}>
      <dl className="ai-facts atr-facts">
        <div>
          <dt>Proposed</dt>
          <dd>
            {actionText({ ...d, plan: detail?.plan })}
            {facts && <span className="muted"> · {facts}</span>}
          </dd>
        </div>
        <div>
          <dt>Reason</dt>
          <dd>{d.reason || <span className="faint">none given</span>}</dd>
        </div>
        <div>
          <dt>Verdict</dt>
          <dd>
            <AiTraderVerdict d={d} /> {d.why && <span>{d.why}</span>}
          </dd>
        </div>
        {placed && (
          <div>
            <dt>Placed</dt>
            <dd className={placed.tone === 'pos' ? 'pos' : placed.tone === 'warn' ? 'warn' : ''}>{placed.text}</dd>
          </div>
        )}
        {shadow && (
          <div>
            <dt>Shadow</dt>
            <dd>{shadow.text}</dd>
          </div>
        )}
        {d.error && !placed && (
          <div>
            <dt>Error</dt>
            <dd className="warn">{d.error}</dd>
          </div>
        )}
        <div>
          <dt>When</dt>
          <dd>
            {d.clockUtc ? `${formatDateTime(d.clockUtc)} IST` : d.clockIst || <span className="faint">not known</span>}
            <span className="muted">
              {' '}
              · {mode.label}
              {mode.means ? `: ${mode.means}` : ''}
              {d.replaySessionId != null ? ` (replay #${d.replaySessionId})` : ''}
            </span>
          </dd>
        </div>
        <div>
          <dt>Model</dt>
          <dd>
            {d.model ? <ModelLabel id={d.model} bare /> : <span className="faint">none answered</span>}
            {d.callId != null && (
              <>
                {' · '}
                <CallLink id={d.callId} />
              </>
            )}
            {confidenceText(d.confidence) && <span className="muted"> · confidence {confidenceText(d.confidence)}</span>}
          </dd>
        </div>
      </dl>

      {q.isPending ? (
        <Loading label="Reading the brief it read…" />
      ) : q.isError || !detail ? (
        <InlineError error={q.error} />
      ) : (
        <>
          <div className="atr-json">
            <div>
              <h4 className="atr-h">
                Plan <span className="faint">as read from the model's answer</span>
              </h4>
              {plan ? <pre className="ai-pre atr-pre">{plan}</pre> : <p className="atr-none">No plan: the answer could not be read as one.</p>}
            </div>
            <div>
              <h4 className="atr-h">
                Result <span className="faint">the contract it priced, and what was placed</span>
              </h4>
              {result ? <pre className="ai-pre atr-pre">{result}</pre> : <p className="atr-none">Nothing priced or placed.</p>}
            </div>
          </div>
          <h4 className="atr-h">
            Brief <span className="faint">exactly as the model read it{detail.briefHash ? ` · #${detail.briefHash.slice(0, 12).toLowerCase()}` : ''}</span>
          </h4>
          {detail.brief ? <pre className="ai-pre atr-brief">{detail.brief}</pre> : <p className="atr-none">The brief was not kept.</p>}
        </>
      )}
    </div>
  )
}

function DecisionRow({ d, open, onToggle, markReplay }: { d: AiTraderDecision; open: boolean; onToggle: () => void; markReplay: boolean }) {
  const v = verdictOf(d)
  const conf = confidenceText(d.confidence)
  const domId = `atr-d-${d.id}`
  const said = d.reason || d.why || d.error
  return (
    <li className={`atr-row atr-row--${v.key}${open ? ' is-open' : ''}`}>
      <button type="button" className="atr-row__btn" aria-expanded={open} aria-controls={open ? domId : undefined} onClick={onToggle}>
        <span className="atr-row__time" title={d.clockUtc ? `${formatDateTime(d.clockUtc)} IST` : undefined}>
          {d.clockIst || '—'}
        </span>
        <span className="atr-row__what">
          <span className="atr-row__action">{actionText(d)}</span>
          <span className={`badge badge--${v.tone} atr-verdict`} title={v.means || undefined}>
            {v.label}
          </span>
          {markReplay && d.mode === 'replay' && (
            <span className="atr-chip" title="Decided on a market replay's clock; it placed nothing">
              replay{d.replaySessionId != null ? ` #${d.replaySessionId}` : ''}
            </span>
          )}
        </span>
        <span className="atr-row__conf" title={conf ? "The model's own confidence" : undefined}>
          {conf && (
            <>
              <span className="atr-row__conf-k">conf. </span>
              {conf}
            </>
          )}
        </span>
        <span className={`atr-row__reason${said ? '' : ' faint'}`}>{said || 'No reason given.'}</span>
      </button>
      {open && <DecisionDetail summary={d} domId={domId} />}
    </li>
  )
}

/**
 * A list of decisions, newest first, a page at a time. A day's list holds
 * that day's replays too (a replay decides on the replayed day's clock), so
 * their rows are marked; a replay's own list says so once, above it
 * (`markReplays` false).
 */
export function AiTraderDecisionList({
  filter,
  pollMs,
  empty,
  label,
  markReplays = true,
}: {
  filter: AiTraderDecisionFilter
  pollMs: AiTraderPoll
  empty: ReactNode
  label: string
  markReplays?: boolean
}) {
  const q = useAiTraderDecisions(filter, pollMs)
  const [openIds, setOpenIds] = useState<ReadonlySet<number>>(() => new Set())
  // A poll can shift the pages under a cursor: one row an id, the first reading kept.
  const rows = useMemo(() => {
    const seen = new Set<number>()
    const out: AiTraderDecision[] = []
    for (const d of q.data?.pages.flatMap((p) => p.items) ?? []) {
      if (!seen.has(d.id)) {
        seen.add(d.id)
        out.push(d)
      }
    }
    return out
  }, [q.data])

  const toggle = (id: number) =>
    setOpenIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  if (q.isPending) return <Loading label="Reading the decisions…" />
  if (q.isError && !q.data) return <InlineError error={q.error} />
  if (rows.length === 0) return <EmptyState>{empty}</EmptyState>

  return (
    <div className="atr">
      {q.isError && (
        <p className="small-note warn ai-flush" role="status">
          The last read failed: showing what was read before.
        </p>
      )}
      <ol className="atr-list" aria-label={label}>
        {rows.map((d) => (
          <DecisionRow key={d.id} d={d} open={openIds.has(d.id)} onToggle={() => toggle(d.id)} markReplay={markReplays} />
        ))}
      </ol>
      <div className="atr-more">
        <span className="faint">
          {rows.length} shown{q.hasNextPage ? ', more older' : ''}
        </span>
        {q.hasNextPage && (
          <button type="button" className="btn btn--sm" disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
            {q.isFetchingNextPage ? 'Reading older decisions…' : 'Load older'}
          </button>
        )}
      </div>
      {q.isFetchNextPageError && <InlineError error={q.error} />}
    </div>
  )
}

// ---------- the shadow book --------------------------------------------------------------

function Ended({ p }: { p: AiTraderShadowPosition }) {
  const e = shadowEnding(p)
  return (
    <span className={`badge badge--${e.tone}`} title={e.means || undefined}>
      {e.label}
    </span>
  )
}

/** "10:00 → 11:42", "10:00 → open". */
function InOut({ p }: { p: AiTraderShadowPosition }) {
  const full = [p.entryUtc && `in ${formatDateTime(p.entryUtc)} IST`, p.exitUtc && `out ${formatDateTime(p.exitUtc)} IST`].filter(Boolean).join(' · ')
  return (
    <span className="atr-book__time" title={full || undefined}>
      {p.entryIst || '—'} → {p.open ? <span className="faint">open</span> : p.exitIst || '—'}
    </span>
  )
}

function Net({ value, charges }: { value: number | null; charges: number | null }) {
  return (
    <span className={`atr-book__net ${netTone(value)}`} title={charges != null ? `After charges of ${formatInrWhole(charges)}` : 'After charges'}>
      {formatInrSigned(value)}
    </span>
  )
}

/**
 * The shadow book: a day's or a replay's positions, oldest first, with the
 * book's counts and net after charges above them. A table on a wide screen,
 * a list on a phone (the net stays on each row's first line).
 */
export function AiTraderShadowBook({
  filter,
  pollMs,
  replay = false,
  empty,
}: {
  filter: AiTraderDecisionFilter
  pollMs: AiTraderPoll
  replay?: boolean
  empty: ReactNode
}) {
  const q = useAiTraderPositions(filter, pollMs)
  const b = q.data
  return (
    <section className="atr-book" aria-label="Shadow book">
      <h4 className="atr-book__h">
        Shadow book
        {b && b.positions > 0 && (
          <span className="atr-book__sum">
            {shadowCountsText(b)} · net after charges <b className={netTone(b.net)}>{formatInrSigned(b.net)}</b> · charges{' '}
            {formatInrWhole(b.charges)}
          </span>
        )}
      </h4>
      <p className="atr-book__how">
        {replay
          ? 'Kept by code for this replay: bought at the ask, checked every minute at the bid against its stop and target, closed at its last mark when the replay ends. Nothing reaches a broker.'
          : 'Kept by code, not placed: bought at the ask, checked every minute at the bid against its stop and target, squared off at 15:30 IST. Nothing reaches a broker.'}
      </p>
      {q.isPending ? (
        <Loading label="Reading the shadow book…" />
      ) : !b ? (
        <InlineError error={q.error} />
      ) : (
        <>
          {q.isError && (
            <p className="small-note warn ai-flush" role="status">
              The last read failed: showing what was read before.
            </p>
          )}
          {b.items.length === 0 ? (
            <p className="atr-none">{empty}</p>
          ) : (
            <>
              <div className="tablewrap atr-book__wide">
                <table className="table atr-book__table">
                  <thead>
                    <tr>
                      <th>In → out</th>
                      <th>Contract</th>
                      <th className="r">Entry</th>
                      <th className="r">Stop / target</th>
                      <th className="r">Mark or exit</th>
                      <th>Ended</th>
                      <th className="r">Net</th>
                    </tr>
                  </thead>
                  <tbody>
                    {b.items.map((p) => {
                      const last = shadowLastPrice(p)
                      return (
                        <tr key={p.id}>
                          <td>
                            <InOut p={p} />
                          </td>
                          <td title={p.symbol || undefined}>
                            <span className="atr-book__contract">{contractText(p)}</span>{' '}
                            <span className="faint">{lotsText(p.lots, p.lotSize)}</span>
                          </td>
                          <td className="r mono">{formatPrice(p.entryPrice)}</td>
                          <td className="r mono">
                            {formatPrice(p.stopLoss)} <span className="faint">/</span> {formatPrice(p.target)}
                          </td>
                          <td className="r mono" title={last.kind === 'mark' ? 'Its last mark: the bid at the last minute check' : 'The price it closed at'}>
                            {formatPrice(last.price)}
                          </td>
                          <td>
                            <Ended p={p} />
                          </td>
                          <td className="r">
                            <Net value={p.net} charges={p.charges} />
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
              <ul className="atr-bl">
                {b.items.map((p) => {
                  const last = shadowLastPrice(p)
                  return (
                    <li key={p.id} className="atr-bl__row">
                      <div className="atr-bl__head">
                        <span className="atr-book__contract">{contractText(p)}</span>
                        <Ended p={p} />
                        <Net value={p.net} charges={p.charges} />
                      </div>
                      <div className="atr-bl__meta">
                        <InOut p={p} />
                        <span>{lotsText(p.lots, p.lotSize)}</span>
                        <span>
                          entry <span className="mono">{formatPrice(p.entryPrice)}</span>
                        </span>
                        <span>
                          stop <span className="mono">{formatPrice(p.stopLoss)}</span> / target <span className="mono">{formatPrice(p.target)}</span>
                        </span>
                        <span>
                          {last.kind === 'mark' ? 'mark' : 'exit'} <span className="mono">{formatPrice(last.price)}</span>
                        </span>
                      </div>
                    </li>
                  )
                })}
              </ul>
            </>
          )}
        </>
      )}
    </section>
  )
}

// ---------- the scoreboard -----------------------------------------------------------------

function Signed({ value }: { value: number | null }) {
  return <span className={`atr-book__net ${netTone(value)}`}>{formatInrSigned(value)}</span>
}

function Partial() {
  return (
    <span className="atr-chip" title="Not in the totals: its looks did not span 09:30–14:30.">
      partial
    </span>
  )
}

/**
 * The scoreboard: its shadow book against a fixed rule anyone could follow,
 * each replay and live shadow day, newest first, after charges. The totals
 * count only full days whose rule result is scored, and say plainly that a
 * handful of days proves nothing.
 */
export function AiTraderScoreboard() {
  const q = useAiTraderScoreboard()
  const { hash } = useLocation()
  const b = q.data
  const t = b?.totals ?? null
  return (
    <section id="ai-trader-scoreboard" className="atr-book atr-score atr-anchor" aria-label="Scoreboard">
      <h4 className="atr-book__h">
        Scoreboard <span className="atr-book__sum">its shadow book against a fixed rule, day by day, after charges</span>
      </h4>
      {q.isPending ? (
        <Loading label="Reading the scoreboard…" />
      ) : !b ? (
        <InlineError error={q.error} />
      ) : (
        <>
          {q.isError && (
            <p className="small-note warn ai-flush" role="status">
              The last read failed: showing what was read before.
            </p>
          )}
          {b.ruleText && (
            <p className="atr-book__how">
              <b>The rule</b>
              {b.rule ? <span className="mono"> ({b.rule})</span> : null}: {b.ruleText} Doing nothing scores ₹0.
            </p>
          )}
          {t ? (
            <>
              <dl className="atr-score__totals">
                <div>
                  <dt>AI net</dt>
                  <dd>
                    <Signed value={t.aiNet} />
                  </dd>
                </div>
                <div>
                  <dt>Rule net</dt>
                  <dd>
                    <Signed value={t.baselineNet} />
                  </dd>
                </div>
                <div>
                  <dt>AI beat the rule</dt>
                  <dd>{beatText(t)}</dd>
                </div>
                <div>
                  <dt>Days in profit</dt>
                  <dd>
                    AI {t.aiPositiveDays} · rule {t.baselinePositiveDays}
                  </dd>
                </div>
                <div>
                  <dt>AI trades</dt>
                  <dd>{t.trades}</dd>
                </div>
                <div>
                  <dt>AI charges</dt>
                  <dd>{formatInrWhole(t.charges)}</dd>
                </div>
              </dl>
              <p className="atr-book__how">
                {sampleNote(t.days)} The totals count only full days (looks from 09:30 or earlier to 14:30 or later) whose rule result is
                scored; nets are after charges.
              </p>
            </>
          ) : (
            <p className="small-note warn ai-flush">The totals did not come back from the API, so they are not known.</p>
          )}
          {b.rows.length === 0 ? (
            <p className="atr-none">No replay or live shadow day yet. Queue recorded days with the AI Trader on Data → Replay.</p>
          ) : (
            <>
              <div className="tablewrap atr-book__wide">
                <table className="table atr-score__table">
                  <thead>
                    <tr>
                      <th>Day</th>
                      <th>Kind</th>
                      <th>Looks</th>
                      <th className="r">AI trades</th>
                      <th className="r">AI net</th>
                      <th>Rule</th>
                      <th className="r">Rule net</th>
                      <th className="r">Difference</th>
                    </tr>
                  </thead>
                  <tbody>
                    {b.rows.map((r) => {
                      const anchor = scoreRowAnchor(r)
                      return (
                        <tr key={anchor} id={anchor} className={`${r.full ? '' : 'atr-score__partial'}${hash === `#${anchor}` ? ' row--selected' : ''}`}>
                          <td className="atr-book__time">{r.day ? shortDay(r.day) : '—'}</td>
                          <td>{scoreKindText(r)}</td>
                          <td>
                            <span className="atr-book__time">{looksSpanText(r) || '—'}</span> <span className="faint">· {r.looks}</span>{' '}
                            {!r.full && <Partial />}
                          </td>
                          <td className="r">
                            {r.positions}
                            {r.open > 0 && <span className="faint"> ({r.open} open)</span>}
                          </td>
                          <td className="r">
                            <Signed value={r.net} />
                          </td>
                          <td title={r.baseline?.note || undefined}>
                            <span className={r.baseline ? '' : 'faint'}>{baselineText(r.baseline)}</span>
                          </td>
                          <td className="r">{r.baseline ? <Signed value={r.baseline.net} /> : <span className="faint">—</span>}</td>
                          <td className="r">{r.vsBaseline != null ? <Signed value={r.vsBaseline} /> : <span className="faint">—</span>}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
              <ul className="atr-bl">
                {b.rows.map((r) => {
                  const anchor = scoreRowAnchor(r)
                  return (
                    <li key={anchor} className={`atr-bl__row${r.full ? '' : ' atr-score__partial'}`}>
                      <div className="atr-bl__head">
                        <span className="atr-book__contract">{r.day ? shortDay(r.day) : '—'}</span>
                        <span className="faint">{scoreKindText(r)}</span>
                        {!r.full && <Partial />}
                        <span className="atr-score__diff" title="The AI's net less the rule's, after charges">
                          {r.vsBaseline != null ? <Signed value={r.vsBaseline} /> : <span className="faint">—</span>}
                        </span>
                      </div>
                      <div className="atr-bl__meta">
                        <span>
                          AI <Signed value={r.net} /> · {r.positions} {r.positions === 1 ? 'trade' : 'trades'}
                        </span>
                        <span title={r.baseline?.note || undefined}>
                          rule: {baselineText(r.baseline)}
                          {r.baseline && (
                            <>
                              {' '}
                              <Signed value={r.baseline.net} />
                            </>
                          )}
                        </span>
                        <span>
                          looks {looksSpanText(r) || '—'} · {r.looks}
                        </span>
                      </div>
                    </li>
                  )
                })}
              </ul>
            </>
          )}
        </>
      )}
    </section>
  )
}
