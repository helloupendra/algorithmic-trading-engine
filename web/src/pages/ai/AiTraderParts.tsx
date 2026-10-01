/**
 * The AI Trader's decisions, as the pages show them: on its panel on
 * AI → Agents (a day's), on Data → Replay (a replay's) and, three at a
 * time, on Today. One row a look: the IST time, what it proposed, the
 * verdict, the model's reason and confidence. A row opens to the whole
 * decision: the verdict's why, what was placed, the plan and result JSON,
 * and the brief exactly as the model read it.
 *
 * Read-only. The words come from lib/aiTrader.ts, so every page says a
 * decision the same way.
 */

import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  actionText,
  confidenceText,
  jsonBlock,
  modeLabel,
  placedText,
  planFacts,
  verdictOf,
} from '../../lib/aiTrader'
import type { AiTraderDecision, AiTraderDecisionFilter } from '../../lib/aiTrader'
import { useAiTraderDecision, useAiTraderDecisions } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
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
  pollMs: number | false
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
