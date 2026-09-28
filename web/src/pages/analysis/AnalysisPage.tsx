/**
 * Analysis → Forecasts: is there anything about the market this desk can
 * predict better than a simple baseline — and can it prove it?
 *
 * Four sections, one at a time (the choice lives in the URL, ?section=):
 *  - Today: a card per index — the range forecast, the chance of a trend day
 *    and of an up close, each beside its baseline; once scored, what happened.
 *  - Scoreboard: every model version's live record against its baseline, with
 *    the 95% interval and calibration. Backtests are shown as history, never
 *    as proof.
 *  - History: past forecasts against their outcomes.
 *  - How this works: the four proof rules, the targets and their baselines.
 *
 * Read-only. The desk's scheduler writes forecasts at 08:50 IST and scores
 * them after 15:50 IST; nothing here can change one. docs/modules/analysis.md
 * is the contract every shape on this page is read from.
 */

import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useForecastModels, useForecastScoreboard, useForecasts, useMarketSession } from '../../lib/queries'
import {
  PROOF,
  SCHEDULE,
  STATUS_META,
  TARGETS,
  TARGET_META,
  UNDERLYINGS,
  UNDERLYING_SYMBOL,
  addDays,
  analysisErrorText,
  asProb,
  asRange,
  bucketEdgesText,
  bucketLabel,
  ciAsSkill,
  ciReading,
  coverageText,
  coverageTone,
  deskAnswer,
  displayStatus,
  forecastCells,
  formatCiSkill,
  formatIstClock,
  formatPct,
  formatPoints,
  formatProb,
  formatProbDelta,
  formatSentiment,
  formatSignedPct,
  formatSkill,
  formatSkillSigned,
  groupScoreboard,
  inputsView,
  intervalBar,
  issuedText,
  istToday,
  issueNote,
  modelShort,
  modelTitle,
  pickSession,
  rangeScale,
  reliabilityLabel,
  reliabilityPoints,
  scoringNote,
  sentimentTone,
  sessionContexts,
  sharedExtent,
  sortForecasts,
  statusMeta,
  targetLabel,
  todayCards,
  verdict,
} from '../../lib/analysis'
import type {
  BacktestPeriod,
  CalibrationBin,
  Forecast,
  ForecastTarget,
  LiveContext,
  ModelGroup,
  RangePrediction,
  ScoreboardRow,
  SessionContext,
  TodayCard,
  ValueNode,
} from '../../lib/analysis'
import { formatDay } from '../../lib/factors'
import { formatAge, formatDateTime, formatPrice } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import './analysis.css'

const SECTIONS = [
  { key: 'today', label: 'Today' },
  { key: 'scoreboard', label: 'Scoreboard' },
  { key: 'history', label: 'History' },
  { key: 'how', label: 'How this works' },
] as const
type SectionKey = (typeof SECTIONS)[number]['key']

function sectionFrom(value: string | null): SectionKey {
  return SECTIONS.find((s) => s.key === value)?.key ?? 'today'
}

/** Re-renders on a clock, so "in 12m" and the day itself keep moving between polls. */
function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}

/** A 403 or a 404 in words; anything else as the error it is. */
function QueryError({ error }: { error: unknown }) {
  const text = analysisErrorText(error)
  if (text) {
    return (
      <div className="alert alert--warn" role="status">
        {text}
      </div>
    )
  }
  return <InlineError error={error} />
}

/** Rows already on screen stay after a failed refresh, with the page saying how old they are. */
function StaleNote({ isError, updatedAt }: { isError: boolean; updatedAt: number }) {
  if (!isError || !updatedAt) return null
  return (
    <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
      Refresh failed — showing what was read {formatAge(new Date(updatedAt).toISOString())}.
    </p>
  )
}

function StatusBadge({ status }: { status: string }) {
  const meta = statusMeta(status)
  return (
    <span title={meta.meaning}>
      <Badge tone={meta.tone}>{meta.label}</Badge>
    </span>
  )
}

// ---------------------------------------------------------------------------
// Small instruments
// ---------------------------------------------------------------------------

/**
 * The range ruler: 0 to a round number, shaded into the index's quiet /
 * normal / wild zones, each labelled with the model's probability for it; the
 * 80% band on top, the median as a line, the baseline's median as a hollow
 * mark and, once scored, the actual range as a dot.
 */
function RangeRuler({
  r,
  baseline,
  actual,
  inside,
}: {
  r: RangePrediction
  baseline: RangePrediction | null
  actual: number | null
  inside: boolean | null
}) {
  const edges = Array.isArray(r.bucketEdges) && r.bucketEdges.length >= 2 ? r.bucketEdges : null
  const scale = rangeScale([r.high80, r.median, baseline?.median, actual, edges?.[1]])
  const at = (v: number) => `${scale.at(v)}%`
  const zones = edges
    ? [
        { key: 'quiet' as const, from: 0, to: edges[0] },
        { key: 'normal' as const, from: edges[0], to: edges[1] },
        { key: 'wild' as const, from: edges[1], to: scale.max },
      ]
    : []
  const ticks = edges ? [0, edges[0], edges[1], scale.max] : [0, scale.max]

  const label =
    `Range forecast ${formatPct(r.median)}, 80% between ${formatPct(r.low80)} and ${formatPct(r.high80)}` +
    (baseline ? `; baseline ${formatPct(baseline.median)}` : '') +
    (actual != null ? `; actual ${formatPct(actual)}` : '')

  return (
    <div className="an-ruler" role="img" aria-label={label}>
      {zones.length > 0 && (
        <div className="an-ruler__zones" aria-hidden="true">
          {zones.map((z) => {
            const p = r.buckets?.[z.key]
            const base = baseline?.buckets?.[z.key]
            return (
              <span
                key={z.key}
                className="an-ruler__zone-label"
                style={{ left: at(z.from), width: `calc(${scale.at(z.to) - scale.at(z.from)}% - 4px)` }}
                title={`${bucketLabel(z.key)}: ${formatProb(p)}${base != null ? ` (baseline ${formatProb(base)})` : ''}`}
              >
                {bucketLabel(z.key)} <b>{formatProb(p)}</b>
              </span>
            )
          })}
        </div>
      )}
      <div className="an-ruler__track" aria-hidden="true">
        {zones.map((z) => (
          <span
            key={z.key}
            className={`an-ruler__zone an-ruler__zone--${z.key}`}
            style={{ left: at(z.from), width: `${scale.at(z.to) - scale.at(z.from)}%` }}
          />
        ))}
        <span
          className="an-ruler__band"
          style={{ left: at(r.low80), width: `${scale.at(r.high80) - scale.at(r.low80)}%` }}
        />
        <span className="an-ruler__median" style={{ left: at(r.median) }} />
        {baseline && <span className="an-ruler__base" style={{ left: at(baseline.median) }} />}
        {actual != null && (
          <span
            className={`an-ruler__actual ${inside === false ? 'an-ruler__actual--out' : ''}`}
            style={{ left: at(actual) }}
          />
        )}
      </div>
      <div className="an-ruler__ticks" aria-hidden="true">
        {ticks.map((t, i) => (
          <span
            key={`${t}-${i}`}
            className={`an-ruler__tick ${i === 0 ? 'is-first' : ''} ${i === ticks.length - 1 ? 'is-last' : ''}`}
            style={{ left: at(t) }}
          >
            {i === 0 ? '0' : formatPct(t)}
          </span>
        ))}
      </div>
    </div>
  )
}

/** A probability as a short bar, with the baseline's value as a tick across it. */
function ProbBar({ p, base }: { p: number | null; base: number | null }) {
  return (
    <span className="an-pbar" aria-hidden="true">
      {p != null && <i style={{ width: `${Math.min(100, Math.max(0, p * 100))}%` }} />}
      {base != null && <b style={{ left: `${Math.min(100, Math.max(0, base * 100))}%` }} />}
    </span>
  )
}

/**
 * The reliability diagram: what a model said against how often it happened,
 * one dot per tenth of probability, sized by how many forecasts are in it. On
 * the diagonal is calibrated; above it, the model was too timid; below, too sure.
 */
function Reliability({ bins, size = 46, axes = false }: { bins: CalibrationBin[] | null; size?: number; axes?: boolean }) {
  const points = reliabilityPoints(bins)
  if (points.length === 0) return <span className="faint">—</span>
  const left = axes ? 26 : 3
  const bottom = axes ? 18 : 3
  const top = 3
  const right = 3
  const w = size - left - right
  const h = size - top - bottom
  const x = (v: number) => left + v * w
  const y = (v: number) => top + (1 - v) * h
  const maxN = Math.max(...points.map((p) => p.n))
  const scaleR = axes ? 2 : 1
  const summary = points.map(reliabilityLabel).join('; ')

  return (
    <svg
      className={`an-rel ${axes ? 'an-rel--axes' : ''}`}
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      role="img"
      aria-label={`Calibration — ${summary}`}
    >
      <title>{summary}</title>
      <rect className="an-rel__frame" x={left} y={top} width={w} height={h} rx={2} />
      {axes &&
        [0.5].map((t) => (
          <g key={t}>
            <line className="an-rel__grid" x1={x(t)} y1={y(0)} x2={x(t)} y2={y(1)} />
            <line className="an-rel__grid" x1={x(0)} y1={y(t)} x2={x(1)} y2={y(t)} />
          </g>
        ))}
      <line className="an-rel__diag" x1={x(0)} y1={y(0)} x2={x(1)} y2={y(1)} />
      {points.length > 1 && (
        <polyline className="an-rel__line" points={points.map((p) => `${x(p.x)},${y(p.y)}`).join(' ')} />
      )}
      {points.map((p) => (
        <circle
          key={`${p.from}`}
          className="an-rel__dot"
          cx={x(p.x)}
          cy={y(p.y)}
          r={(1.4 + 2.2 * Math.sqrt(p.n / maxN)) * scaleR}
        >
          <title>{reliabilityLabel(p)}</title>
        </circle>
      ))}
      {axes && (
        <g className="an-rel__axis">
          <text x={x(0)} y={size - 5} textAnchor="start">0</text>
          <text x={x(0.5)} y={size - 5} textAnchor="middle">said 50%</text>
          <text x={x(1)} y={size - 5} textAnchor="end">100</text>
          <text x={left - 4} y={y(0)} textAnchor="end" dominantBaseline="middle">0</text>
          <text x={left - 4} y={y(1)} textAnchor="end" dominantBaseline="hanging">100</text>
          <text x={left - 4} y={y(0.5)} textAnchor="end" dominantBaseline="middle">50</text>
        </g>
      )}
    </svg>
  )
}

/** Live skill's 95% interval, rescaled to skill, as a bar across a zero line. */
function CiBar({ row, extent }: { row: ScoreboardRow; extent?: number }) {
  const ci = ciAsSkill(row)
  if (!ci) return <span className="faint small">no interval yet</span>
  const bar = intervalBar(ci.low, ci.high, row.skill, extent)
  const tone = ci.low > 0 ? 'pos' : ci.high < 0 ? 'neg' : 'mid'
  const raw =
    row.diffCiLow != null && row.diffCiHigh != null
      ? ` Mean of (baseline loss − loss), 95% bootstrap: ${row.diffCiLow.toFixed(4)} to ${row.diffCiHigh.toFixed(4)}.`
      : ''
  return (
    <span className="an-ci" title={`${ciReading(row)}${raw}`}>
      <span className="an-ci__track" aria-hidden="true">
        <span className="an-ci__zero" />
        <span className={`an-ci__range an-ci__range--${tone}`} style={{ left: `${bar.from}%`, width: `${Math.max(1, bar.to - bar.from)}%` }} />
        {bar.point != null && <span className="an-ci__point" style={{ left: `${bar.point}%` }} />}
      </span>
      <span className="an-ci__text mono">{formatCiSkill(row)}</span>
    </span>
  )
}

/**
 * "42 / 60" with a hairline of progress toward the count the pass mark needs.
 * Past it the count stands alone: "75 / 60" reads like a score out of sixty.
 */
function LiveCount({ n }: { n: number }) {
  const pct = Math.min(100, (n / PROOF.provenAt) * 100)
  const reached = n >= PROOF.provenAt
  return (
    <span
      className="an-live"
      title={
        reached
          ? `${n} live forecasts scored: enough for a verdict. Retirement is judged at ${PROOF.retiredAt}.`
          : `${n} live forecasts scored. ${PROOF.provenAt} are needed before any model can be called proven.`
      }
    >
      <span className="mono">
        {n} {!reached && <span className="faint">/ {PROOF.provenAt}</span>}
      </span>
      <span className="an-live__bar" aria-hidden="true">
        <i style={{ width: `${pct}%` }} />
      </span>
    </span>
  )
}

// ---------------------------------------------------------------------------
// Today
// ---------------------------------------------------------------------------

function ModelTag({ f, group }: { f: Forecast; group?: ModelGroup }) {
  return (
    <span className="an-model">
      <span className="mono an-model__key" title={`${modelTitle(f.modelKey, f.modelVersion, group?.description)}\nIssued ${formatDateTime(f.issuedUtc)} IST.`}>
        {f.modelKey}
      </span>
      {group && <StatusBadge status={displayStatus(group.all)} />}
    </span>
  )
}

function RangeBlock({ f, others, group }: { f: Forecast; others: Forecast[]; group?: ModelGroup }) {
  const r = asRange(f.prediction)
  const b = asRange(f.baseline)
  if (!r) return <p className="empty">This range forecast has no median.</p>
  const actual = f.outcome?.range ?? null
  const inside = f.scores?.metrics?.covered80 ?? (actual != null ? actual >= r.low80 && actual <= r.high80 : null)

  return (
    <section className="an-block" aria-label="Range">
      <div className="an-block__head">
        <span className="an-label">Range</span>
        <span className="an-range__value mono">{formatPct(r.median)}</span>
        <span className="an-range__pts mono muted">{formatPoints(r.points?.median)}</span>
        <span className="an-block__base small muted">
          baseline <span className="mono">{formatPct(b?.median)}</span>
        </span>
      </div>
      <RangeRuler r={r} baseline={b} actual={actual} inside={inside} />
      <p className="an-block__line small muted">
        80% between <span className="mono">{formatPct(r.low80)}</span> and <span className="mono">{formatPct(r.high80)}</span>
        {r.points && (
          <span className="faint">
            {' '}
            · ≈{Math.round(r.points.low80).toLocaleString('en-IN')}–{Math.round(r.points.high80).toLocaleString('en-IN')} pts
          </span>
        )}
      </p>
      {f.outcome && (
        <p className="an-block__line small an-outcome">
          <span className="an-outcome__label">Actual</span>{' '}
          <span className="mono">{formatPct(f.outcome.range)}</span> · {bucketLabel(f.outcome.bucket)} ·{' '}
          {inside === true ? (
            <span className="pos">inside the 80% band</span>
          ) : inside === false ? (
            <span className="neg">outside the 80% band</span>
          ) : (
            <span className="faint">band not checked</span>
          )}
        </p>
      )}
      <div className="an-block__foot">
        <ModelTag f={f} group={group} />
        {others.length > 0 && (
          <span className="an-also small faint">
            also{' '}
            {others.map((o, i) => {
              const or = asRange(o.prediction)
              return (
                <span key={o.id} className="mono" title={modelTitle(o.modelKey, o.modelVersion)}>
                  {i > 0 ? ' · ' : ''}
                  {o.modelKey} {or ? `${formatPct(or.median)} (${or.low80.toFixed(2)}–${formatPct(or.high80)})` : '—'}
                </span>
              )
            })}
          </span>
        )}
      </div>
    </section>
  )
}

function ProbRow({
  label,
  f,
  group,
  happened,
  yes,
  no,
}: {
  label: string
  f: Forecast | undefined
  group?: ModelGroup
  happened: boolean | null
  yes: string
  no: string
}) {
  if (!f) {
    return (
      <div className="an-prob">
        <span className="an-label">{label}</span>
        <span className="faint small an-prob__none">not issued</span>
      </div>
    )
  }
  const p = asProb(f.prediction)
  const base = asProb(f.baseline)
  return (
    <div className="an-prob">
      <span className="an-label">{label}</span>
      <span className="an-prob__value mono">{formatProb(p)}</span>
      <ProbBar p={p} base={base} />
      <span className="an-prob__base small muted">
        baseline <span className="mono">{formatProb(base)}</span>{' '}
        <span className="faint">{formatProbDelta(p, base)}</span>
      </span>
      {happened != null && (
        <span className={`an-prob__out small ${happened ? 'an-prob__out--yes' : ''}`}>{happened ? yes : no}</span>
      )}
      <span className="an-prob__model">
        <ModelTag f={f} group={group} />
      </span>
    </div>
  )
}

/**
 * A value the page has no particular layout for, as nested label/value
 * lists (lib/analysis describeValue). Never JSON on screen.
 */
function ValueView({ node }: { node: ValueNode }) {
  if (node.kind === 'text') return <>{node.text}</>
  if (node.kind === 'items') {
    return (
      <ol className="an-tree an-tree--items">
        {node.items.map((item, i) => (
          <li key={i}>
            <ValueView node={item} />
          </li>
        ))}
      </ol>
    )
  }
  return (
    <dl className="an-tree">
      {node.fields.map((f) => (
        <div key={f.key}>
          <dt>{f.label}</dt>
          <dd className={nestClass(f.node)}>
            <ValueView node={f.node} />
          </dd>
        </div>
      ))}
    </dl>
  )
}

/** A value that is itself a list or a set of fields takes the whole row, under its label. */
function nestClass(node: ValueNode, base = ''): string | undefined {
  const cls = `${base}${node.kind === 'text' ? '' : ' an-tree__nest'}`.trim()
  return cls || undefined
}

/**
 * What the card's models saw: each of the morning's facts once, under the
 * models that record it, then what differs by model (the history each was
 * fitted on) as a small table.
 */
function InputsList({ forecasts, groups }: { forecasts: Forecast[]; groups: Map<string, ModelGroup> }) {
  const v = inputsView(forecasts)
  if (!v || (v.groups.length === 0 && !v.perModel)) return null
  const about = (key: string, version: string) => modelTitle(key, version, groups.get(`${key}@${version}`)?.description)
  const versionOf = new Map(forecasts.map((f) => [f.modelKey, f.modelVersion]))
  return (
    <details className="an-inputs">
      <summary>
        What the models saw
        {v.prevSession && <span className="faint"> · inputs from {formatDay(v.prevSession)}</span>}
      </summary>
      <div className="an-inputs__body">
        {v.groups.map((g) => (
          <div key={g.models.join(' ')} className="an-inputs__group">
            <p className="an-inputs__who">
              {g.all ? (
                'All the models'
              ) : (
                g.models.map((m, i) => (
                  <span key={m}>
                    {i > 0 && <span className="faint"> · </span>}
                    <span className="mono" title={about(m, versionOf.get(m) ?? '')}>
                      {m}
                    </span>
                  </span>
                ))
              )}
            </p>
            <dl className="an-facts an-facts--sm">
              {g.facts.map((f) => (
                <div key={f.key}>
                  <dt>{f.label}</dt>
                  <dd className={nestClass(f.value, 'mono')}>
                    <ValueView node={f.value} />
                  </dd>
                </div>
              ))}
            </dl>
          </div>
        ))}
        {v.perModel && (
          <div className="tablewrap an-inputs__wrap">
            <table className="table an-inputs__table">
              <thead>
                <tr>
                  <th scope="col">Model</th>
                  {v.perModel.keys.map((k) => (
                    <th key={k.key} scope="col" className="r">
                      {k.label}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {v.perModel.rows.map((r) => (
                  <tr key={r.model}>
                    <th scope="row" className="mono" title={about(r.modelKey, r.modelVersion)}>
                      {r.model}
                    </th>
                    {r.cells.map((cell, i) => (
                      <td key={v.perModel!.keys[i].key} className="r mono">
                        {cell ? <ValueView node={cell} /> : <span className="faint">—</span>}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </details>
  )
}

// ---------- the pre-open context ----------

/** "No snapshot this morning" — a part's own sentence, capitalised. */
function sentence(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1)
}

function signedTone(value: number | null): string {
  if (value == null || Number(value.toFixed(2)) === 0) return ''
  return value > 0 ? 'pos' : 'neg'
}

/** A count with Indian grouping; "—" when the record has none. */
function count(n: number | null): string {
  return n == null ? '—' : n.toLocaleString('en-IN')
}

function Fact({ label, value, title, className = '' }: { label: string; value: ReactNode; title?: string; className?: string }) {
  return (
    <div title={title}>
      <dt>{label}</dt>
      <dd className={`mono ${className}`.trim()}>{value}</dd>
    </div>
  )
}

function GiftFacts({ c }: { c: LiveContext }) {
  const g = c.gift
  return (
    <div className="an-ctx__part">
      <h4 className="an-label">GIFT Nifty</h4>
      {g ? (
        <>
          <dl className="an-facts">
            <Fact
              label="Gap to NIFTY's previous close"
              value={formatSignedPct(g.gapPct)}
              className={signedTone(g.gapPct)}
              title="The morning's latest GIFT Nifty snapshot against NIFTY's previous close."
            />
            <Fact label="Change (vendor)" value={formatSignedPct(g.changePct)} className={signedTone(g.changePct)} />
            <Fact
              label="Snapshot"
              value={
                <>
                  {formatIstClock(g.asOfUtc)}
                  {g.fetchedUtc && <span className="faint"> · read {formatIstClock(g.fetchedUtc).replace(' IST', '')}</span>}
                </>
              }
              title="As of: the source's own time. Read: when the desk fetched it."
            />
          </dl>
          <p className="an-ctx__fine small faint">A future: the gap includes its basis to spot, a few tenths of a percent.</p>
        </>
      ) : (
        <p className="an-ctx__fine small muted">{c.giftNote ? sentence(c.giftNote) : 'Not recorded.'}</p>
      )}
    </div>
  )
}

function EarningsFacts({ c }: { c: LiveContext }) {
  const e = c.earnings
  return (
    <div className="an-ctx__part">
      <h4 className="an-label">NIFTY 50 results</h4>
      {e ? (
        <dl className="an-facts">
          <Fact label="Today" value={count(e.today)} title="NIFTY-50 companies with results on this session." />
          <Fact
            label="Since the previous session"
            value={count(e.sincePrev)}
            title="NIFTY-50 companies with results from the previous session to this one, weekend results included."
          />
        </dl>
      ) : (
        <p className="an-ctx__fine small muted">{c.earningsNote ? sentence(c.earningsNote) : 'Not recorded.'}</p>
      )}
    </div>
  )
}

/** The news model's mean reading, −1 … +1: a bar from the middle, and the number. */
function SentimentBar({ value }: { value: number | null }) {
  const tone = sentimentTone(value)
  if (value == null || tone == null) return <span className="faint">—</span>
  const v = Math.max(-1, Math.min(1, value))
  const from = v >= 0 ? 50 : 50 + v * 50
  const width = Math.abs(v) * 50
  return (
    <span className={`an-senti an-senti--${tone}`}>
      <span className="an-senti__track" aria-hidden="true">
        <i style={{ left: `${from}%`, width: `${Math.max(width, 1)}%` }} />
      </span>
      <b className="mono">{formatSentiment(value)}</b>
    </span>
  )
}

/** Highest importance 0–3, as three pips. */
function Importance({ value }: { value: number | null }) {
  if (value == null) return <span className="faint">—</span>
  const n = Math.max(0, Math.min(3, Math.round(value)))
  return (
    <span className="an-imp" role="img" aria-label={`${n} of 3`} title={`Highest importance among the scored headlines: ${n} of 3`}>
      {[0, 1, 2].map((i) => (
        <i key={i} className={i < n ? 'is-on' : ''} />
      ))}
    </span>
  )
}

function NewsTable({ c, since }: { c: LiveContext; since: string | null }) {
  const rows = c.news
  const from = since ? `${formatDay(since)}, 15:30 IST` : 'the previous close'
  let body: ReactNode
  if (rows && rows.length > 0) {
    body = (
      <div className="tablewrap an-news__wrap">
        <table className="table an-news">
          <thead>
            <tr>
              <th scope="col">Category</th>
              <th scope="col" className="r an-wide" title="Headlines first seen between the previous close and the forecasts">
                Headlines
              </th>
              <th scope="col" className="r an-wide" title="How many of them the news model had scored by then">
                Scored
              </th>
              <th scope="col" className="r an-narrow" title="Headlines scored by the news model, of those first seen">
                Scored
              </th>
              <th scope="col" title="Mean reading of the scored headlines, −1 to +1: the words, not a signal">
                Sentiment
              </th>
              <th scope="col" title="Highest importance among the scored headlines, 0 to 3 (rules: results, policy, deals, …)">
                <span className="an-wide">Importance</span>
                <span className="an-narrow">Imp.</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={r.key} className={i < rows.length - 1 && rows[i + 1].group !== r.group ? 'an-news__end' : undefined}>
                <th scope="row" title={r.group === 'filings' ? 'NSE announcements by NIFTY-50 companies' : undefined}>
                  {r.label}
                </th>
                <td className="r mono an-wide">{count(r.n)}</td>
                <td className="r mono muted an-wide">{count(r.scored)}</td>
                <td className="r mono an-narrow">
                  {count(r.scored)} <span className="faint">of {count(r.n)}</span>
                </td>
                <td>
                  <SentimentBar value={r.sentiment} />
                </td>
                <td>
                  <Importance value={r.maxImportance} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    )
  } else if (rows) {
    body = <p className="an-ctx__fine small muted">No headlines were first seen between {from} and the forecasts.</p>
  } else {
    body = <p className="an-ctx__fine small muted">{c.newsNote ? sentence(c.newsNote) : 'Not recorded.'}</p>
  }
  return (
    <div className="an-ctx__part an-ctx__news">
      <h4 className="an-label">News since the previous close</h4>
      {body}
      {rows && rows.length > 0 && (
        <p className="an-ctx__fine small faint">
          Headlines first seen from {from} until the forecasts were written. Sentiment is the news model's mean reading
          of the words, −1 to +1, not a signal; importance is the highest among them, 0 to 3.
        </p>
      )}
    </div>
  )
}

/**
 * The pre-open context recorded with the session's forecasts (inputs.liveOnly):
 * the GIFT Nifty gap, the earnings load and the news by category. No model
 * reads it; the page says so beside it, and claims nothing about it.
 */
function ContextPanel({ ctx, since, several }: { ctx: SessionContext; since: string | null; several: boolean }) {
  const c = ctx.context
  const notUsed = c.usedByModels === false
  return (
    <section className="an-ctx" aria-label="Pre-open context">
      <header className="an-ctx__head">
        <h3 className="an-ctx__title">Pre-open context</h3>
        <span className="small faint">
          recorded with {several ? `the ${ctx.underlyings.join(', ')} forecasts` : 'the forecasts'},{' '}
          {formatIstClock(ctx.issuedUtc)}
        </span>
        {notUsed && (
          <span className="an-ctx__badge">
            <Badge tone="neutral">Not used by the models</Badge>
          </span>
        )}
      </header>
      <div className="an-ctx__grid">
        {notUsed && (
          <p className="an-ctx__note small muted">
            Shown for reference, not used by the models. None of this has a history to backtest, so no model reads it;
            it is recorded with every forecast so that, once there are enough live sessions, whether it would have
            helped can be tested on forecasts written before the open.
          </p>
        )}
        <div className="an-ctx__side">
          <GiftFacts c={c} />
          <EarningsFacts c={c} />
        </div>
        <NewsTable c={c} since={since} />
      </div>
      {c.other.length > 0 && (
        <div className="an-ctx__part">
          <h4 className="an-label">Also recorded</h4>
          <dl className="an-tree an-tree--top">
            {c.other.map((o) => (
              <div key={o.key}>
                <dt>{o.label}</dt>
                <dd className={nestClass(o.node)}>
                  <ValueView node={o.node} />
                </dd>
              </div>
            ))}
          </dl>
        </div>
      )}
    </section>
  )
}

function IndexCard({ card, groups, today }: { card: TodayCard; groups: Map<string, ModelGroup>; today: string }) {
  const lead = card.range[0]
  const all = [...card.range, ...card.trend, ...card.direction]
  const groupOf = (f: Forecast | undefined) => (f ? groups.get(`${f.modelKey}@${f.modelVersion}`) : undefined)
  const outcome = all.find((f) => f.outcome)?.outcome ?? null
  const prevClose = asRange(lead?.prediction)?.prevClose ?? null
  const pending = all.find((f) => !f.outcome)
  const scoring = pending ? scoringNote(pending, today) : null

  return (
    <article className="an-card">
      <header className="an-card__head">
        <h3 className="an-card__name" title={UNDERLYING_SYMBOL[card.underlying as keyof typeof UNDERLYING_SYMBOL]}>
          {card.underlying}
        </h3>
        {prevClose != null && (
          <span className="an-card__meta small muted">
            prev close <span className="mono">{formatPrice(prevClose)}</span>
          </span>
        )}
      </header>

      {lead ? (
        <RangeBlock f={lead} others={card.range.slice(1)} group={groupOf(lead)} />
      ) : (
        <p className="empty">No range forecast for this session.</p>
      )}

      <section className="an-block an-block--probs" aria-label="Probabilities">
        <ProbRow
          label="Trend day"
          f={card.trend[0]}
          group={groupOf(card.trend[0])}
          happened={outcome ? outcome.trendDay : null}
          yes="trend day"
          no="no trend day"
        />
        <ProbRow
          label="Up close"
          f={card.direction[0]}
          group={groupOf(card.direction[0])}
          happened={outcome ? outcome.up : null}
          yes="closed up"
          no="closed down"
        />
        {outcome && (
          <p className="an-block__line small faint">
            Session {formatPrice(outcome.open)} → {formatPrice(outcome.close)} · high {formatPrice(outcome.high)} · low{' '}
            {formatPrice(outcome.low)} · efficiency {outcome.efficiency.toFixed(2)}
          </p>
        )}
      </section>

      {scoring && <p className={`an-card__note small ${scoring.tone === 'warn' ? 'warn' : 'faint'}`}>{scoring.text}</p>}
      <InputsList forecasts={all} groups={groups} />
    </article>
  )
}

const RELATION_LABEL = { today: 'Today', next: 'Next session', last: 'Last session' } as const

function TodaySection({ groups, modelsKnown, nowMs }: { groups: ModelGroup[]; modelsKnown: boolean | null; nowMs: number }) {
  const today = istToday(nowMs)
  const list = useForecasts({ from: addDays(today, -14), to: addDays(today, 7) })
  const session = useMarketSession()
  const byId = useMemo(() => new Map(groups.map((g) => [g.id, g])), [groups])

  if (list.isPending) return <Loading label="Reading today's forecasts…" />
  if (!list.data) return <QueryError error={list.error} />

  const pick = pickSession(list.data, today)
  const note = issueNote({
    nowMs,
    hasToday: pick?.relation === 'today',
    isTradingDay: session.data?.isTradingDay,
    nextOpenUtc: session.data?.nextMarketOpenUtc,
  })

  if (!pick) {
    // With no model registered there is nothing the 08:50 job could have
    // issued, so its "nothing was issued today" warning would point at the
    // wrong thing.
    return (
      <>
        <StaleNote isError={list.isError} updatedAt={list.dataUpdatedAt} />
        <EmptyState>
          {modelsKnown === false
            ? `Nothing has been forecast yet: no model is registered. A model shows up on the scoreboard, with its backtest, as soon as it is registered, and forecasts from the next trading morning at ${SCHEDULE.issue} IST.`
            : `No forecast in the last two weeks. ${note?.text ?? `Forecasts are issued at ${SCHEDULE.issue} IST on trading days.`}`}
        </EmptyState>
      </>
    )
  }

  const cards = todayCards(list.data, pick.sessionDate, groups)
  const sessionForecasts = list.data.filter((f) => f.sessionDate === pick.sessionDate)
  const issued = issuedText(sessionForecasts)
  const contexts = sessionContexts(sessionForecasts)
  const since = inputsView(sessionForecasts)?.prevSession ?? null

  return (
    <div className="an-today">
      <StaleNote isError={list.isError} updatedAt={list.dataUpdatedAt} />
      <div className="an-session">
        <div className="an-session__when">
          <span className="an-session__rel">{RELATION_LABEL[pick.relation]}</span>
          <span className="an-session__date">{formatDay(pick.sessionDate)}</span>
          {issued && <span className="an-session__issued small faint">{issued}</span>}
        </div>
        {note && <p className={`an-session__note small ${note.tone === 'warn' ? 'warn' : 'muted'}`}>{note.text}</p>}
        <p className="an-legend small faint" aria-hidden="true">
          <span className="an-legend__band" /> 80% band <span className="an-legend__median" /> median{' '}
          <span className="an-legend__base" /> baseline <span className="an-legend__actual" /> actual
        </p>
      </div>
      <div className="an-cards">
        {cards.map((card) => (
          <IndexCard key={card.underlying} card={card} groups={byId} today={today} />
        ))}
      </div>
      {contexts.map((ctx, i) => (
        <ContextPanel key={i} ctx={ctx} since={since} several={contexts.length > 1} />
      ))}
      <p className="small-note muted">
        Each number sits beside its baseline: a model is only useful where it differs from the baseline and is right
        to. One session proves nothing either way — the scoreboard is where a model is judged. Times are IST.
      </p>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Scoreboard
// ---------------------------------------------------------------------------

const BOARD_COLUMNS = 8

function historyCell(backtest: ModelGroup['backtest']) {
  const parts: Array<[string, BacktestPeriod | null | undefined]> = [
    ['design', backtest?.design],
    ['validation', backtest?.validation],
    ['holdout', backtest?.holdout],
  ]
  if (parts.every(([, p]) => !p)) return <span className="faint">none registered</span>
  return (
    <span className="an-hist mono">
      {parts.map(([name, p], i) => (
        <span key={name} title={p ? `${name} ${p.from} → ${p.to}: ${p.n} sessions, loss ${p.loss} vs baseline ${p.baselineLoss}` : `${name}: none`}>
          {i > 0 && <span className="faint"> / </span>}
          {p ? formatSkillSigned(p.skill) : '—'}
        </span>
      ))}
    </span>
  )
}

function BoardRow({
  row,
  group,
  sub,
  open,
  onToggle,
  extent,
}: {
  row: ScoreboardRow
  group: ModelGroup
  sub?: boolean
  open?: boolean
  onToggle?: () => void
  extent?: number
}) {
  const range = row.target === 'range'
  return (
    <tr
      className={`${sub ? 'an-board__sub' : 'an-board__main'} ${open ? 'an-board__main--open' : ''}`}
      onClick={onToggle}
      style={onToggle ? { cursor: 'pointer' } : undefined}
    >
      <td>
        {sub ? (
          <span className="an-board__index">{row.underlying}</span>
        ) : (
          <button type="button" className="an-board__toggle" aria-expanded={open}>
            <span className="an-board__caret" aria-hidden="true">
              {open ? '▾' : '▸'}
            </span>
            <span title={modelTitle(group.modelKey, group.modelVersion, group.description)}>
              <span className="mono an-board__key">{group.modelKey}</span>
              <span className="an-board__meta">
                {targetLabel(group.target)}
                {modelShort(group.modelKey) && ` · ${modelShort(group.modelKey)}`} ·{' '}
                <span className="mono">{group.modelVersion}</span>
              </span>
            </span>
          </button>
        )}
      </td>
      <td>
        <StatusBadge status={displayStatus(row)} />
      </td>
      <td>
        <LiveCount n={row.liveCount} />
      </td>
      <td className="an-board__skill">{formatSkill(row.skill, true)}</td>
      <td>
        <CiBar row={row} extent={extent} />
      </td>
      <td>{sub ? <span className="faint">{historyUnderlying(group, row.underlying)}</span> : historyCell(group.backtest)}</td>
      <td className={range ? coverageTone(row.coverage80, row.liveCount) ?? '' : ''}>
        {range ? (
          <span className="mono" title={coverageText(row.coverage80)}>
            {formatProb(row.coverage80)}
          </span>
        ) : (
          <span className="faint">—</span>
        )}
      </td>
      <td className="an-board__cal">
        <Reliability bins={row.calibration} />
      </td>
    </tr>
  )
}

function historyUnderlying(group: ModelGroup, underlying: string): string {
  const u = group.backtest?.byUnderlying?.[underlying]
  return u ? `${formatSkillSigned(u.skill)} over ${u.n.toLocaleString('en-IN')}` : '—'
}

function PeriodTable({ group }: { group: ModelGroup }) {
  const b = group.backtest
  const periods: Array<[string, BacktestPeriod | null | undefined]> = [
    ['Design', b?.design],
    ['Validation', b?.validation],
    ['Holdout', b?.holdout],
  ]
  if (!b || periods.every(([, p]) => !p)) {
    return <p className="small muted">No backtest was registered with this version.</p>
  }
  return (
    <table className="table an-periods">
      <thead>
        <tr>
          <th>Period</th>
          <th>Sessions</th>
          <th className="r">n</th>
          <th className="r">Loss</th>
          <th className="r">Baseline</th>
          <th className="r">Skill</th>
        </tr>
      </thead>
      <tbody>
        {periods.map(([name, p]) => (
          <tr key={name}>
            <td>{name}</td>
            <td className="mono" title={p ? `${p.from} → ${p.to}` : undefined}>
              {p ? `${p.from.slice(0, 7)} → ${p.to.slice(0, 7)}` : '—'}
            </td>
            <td className="r mono">{p ? p.n.toLocaleString('en-IN') : '—'}</td>
            <td className="r mono">{p ? p.loss.toFixed(3) : '—'}</td>
            <td className="r mono">{p ? p.baselineLoss.toFixed(3) : '—'}</td>
            <td className="r mono">{p ? formatSkillSigned(p.skill) : '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function BoardDetail({ group }: { group: ModelGroup }) {
  const v = verdict(group.all)
  const meta = statusMeta(group.all.status)
  const tried = group.backtest?.configurationsTried
  return (
    <tr className="an-board__detail">
      <td colSpan={BOARD_COLUMNS}>
        <div className="an-detail">
          <div className="an-detail__main">
            <p className={`an-verdict an-verdict--${v.tone}`}>{v.text}</p>
            {group.description && <p className="an-detail__text">{group.description}</p>}
            <dl className="an-detail__facts">
              <div>
                <dt>The scoreboard says</dt>
                <dd>{group.all.statusReason || '—'}</dd>
              </div>
              <div>
                <dt>{meta.label}</dt>
                <dd>{meta.meaning}</dd>
              </div>
              <div>
                <dt>Interval</dt>
                <dd>{ciReading(group.all)}</dd>
              </div>
              {group.registeredUtc && (
                <div>
                  <dt>Registered</dt>
                  <dd>{formatDateTime(group.registeredUtc)} IST</dd>
                </div>
              )}
              <div>
                <dt>Live sessions</dt>
                <dd className="mono">
                  {group.all.firstSession ? `${group.all.firstSession} → ${group.all.lastSession}` : 'none scored yet'}
                </dd>
              </div>
              {group.target === 'range' && (
                <div>
                  <dt>Coverage</dt>
                  <dd>
                    {group.all.coverage80 == null ? 'None scored yet' : coverageText(group.all.coverage80)} — a
                    calibrated band holds about 80%.
                  </dd>
                </div>
              )}
            </dl>
          </div>
          <div className="an-detail__history">
            <h4 className="section-title">Backtest — history, not proof</h4>
            <PeriodTable group={group} />
            {tried != null && (
              <p className="small muted">
                {tried} configuration{tried === 1 ? '' : 's'} tried for this target, sibling models included. The more
                that are tried, the more a good backtest can be luck — which is why only live forecasts count.
              </p>
            )}
            {group.backtest?.notes && <p className="small muted">{group.backtest.notes}</p>}
          </div>
          {reliabilityPoints(group.all.calibration).length > 0 && (
            <figure className="an-detail__cal">
              <Reliability bins={group.all.calibration} size={150} axes />
              <figcaption className="small faint">
                {group.target === 'range' ? 'Quiet / normal / wild probabilities: ' : ''}said (across) against happened
                (up), in tenths. On the diagonal is calibrated.
              </figcaption>
            </figure>
          )}
        </div>
      </td>
    </tr>
  )
}

function ModelRows({ group, extent }: { group: ModelGroup; extent?: number }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <BoardRow row={group.all} group={group} open={open} onToggle={() => setOpen((o) => !o)} extent={extent} />
      {open && group.perUnderlying.map((row) => <BoardRow key={row.underlying} row={row} group={group} sub extent={extent} />)}
      {open && <BoardDetail group={group} />}
    </>
  )
}

function ScoreboardSection({
  groups,
  board,
  modelsError,
}: {
  groups: ModelGroup[]
  board: ReturnType<typeof useForecastScoreboard>
  modelsError: unknown
}) {
  if (board.isPending) return <Loading label="Reading the scoreboard…" />
  if (!board.data) return <QueryError error={board.error} />
  if (groups.length === 0) {
    return (
      <EmptyState>
        No model is registered yet. Each model version appears here with its backtest as soon as it is registered,
        and starts collecting live forecasts the next trading morning at {SCHEDULE.issue} IST.
      </EmptyState>
    )
  }
  const extent = sharedExtent(groups.flatMap((g) => [g.all, ...g.perUnderlying]))

  return (
    <div className="an-board-wrap">
      <StaleNote isError={board.isError} updatedAt={board.dataUpdatedAt} />
      {modelsError != null && (
        <p className="small-note warn" style={{ margin: '0 0 8px' }}>
          The model list did not load, so models registered but not yet scored may be missing below.
        </p>
      )}
      <div className="tablewrap">
        <table className="table an-board">
          <thead>
            <tr>
              <th>Model</th>
              <th>Status</th>
              <th>Live</th>
              <th>Live vs baseline</th>
              <th title="The 95% bootstrap interval of the live improvement, on the skill scale. Crossing the centre line means it could be no better than the baseline.">
                95% interval
              </th>
              <th title="Walk-forward backtest skill: design / validation / holdout. History is shown, but it never makes a model proven.">
                History — not proof
              </th>
              <th title="How often the actual range fell inside the 80% band. A calibrated band holds about 80%.">Coverage</th>
              <th title="Reliability: what it said against how often it happened.">Calibration</th>
            </tr>
          </thead>
          <tbody>
            {groups.map((g) => (
              <ModelRows key={g.id} group={g} extent={extent} />
            ))}
          </tbody>
        </table>
      </div>
      <p className="small-note muted">
        One row per model version across all three indices; open a row for each index, the verdict, the backtest and
        the calibration. Skill is how much lower the model's average loss is than the baseline's. History columns are
        design / validation / holdout. Refreshes every 60 s.
      </p>
    </div>
  )
}

// ---------------------------------------------------------------------------
// History
// ---------------------------------------------------------------------------

const PERIODS = [
  { key: '30', label: '30 days', days: 30 },
  { key: '90', label: '90 days', days: 90 },
  { key: 'all', label: 'All', days: null },
] as const
type PeriodKey = (typeof PERIODS)[number]['key']

const HISTORY_CAP = 500

function Seg<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string
  value: T
  options: ReadonlyArray<{ key: T; label: string }>
  onChange: (v: T) => void
}) {
  return (
    <div className="seg" role="group" aria-label={label}>
      {options.map((o) => (
        <button
          key={o.key}
          type="button"
          className={`seg__btn ${value === o.key ? 'is-active' : ''}`}
          aria-pressed={value === o.key}
          onClick={() => onChange(o.key)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

function HistorySection({ nowMs }: { nowMs: number }) {
  const [target, setTarget] = useState<ForecastTarget | ''>('')
  const [underlying, setUnderlying] = useState('')
  const [periodKey, setPeriodKey] = useState<PeriodKey>('30')
  const today = istToday(nowMs)
  const period = PERIODS.find((p) => p.key === periodKey) ?? PERIODS[0]
  const list = useForecasts({
    from: period.days != null ? addDays(today, -period.days) : undefined,
    target,
    underlying,
  })
  const rows = useMemo(() => (list.data ? sortForecasts(list.data) : undefined), [list.data])

  let body: ReactNode
  if (list.isPending) body = <Loading label="Reading past forecasts…" />
  else if (!rows) body = <QueryError error={list.error} />
  else if (rows.length === 0) body = <EmptyState>No forecasts for these filters yet.</EmptyState>
  else {
    const shown = rows.slice(0, HISTORY_CAP)
    body = (
      <>
        <StaleNote isError={list.isError} updatedAt={list.dataUpdatedAt} />
        <div className="tablewrap tablewrap--tall">
          <table className="table an-history">
            <thead>
              <tr>
                <th>Session</th>
                <th>Index</th>
                <th>Target</th>
                <th>Model</th>
                <th className="r">Forecast</th>
                <th className="r">Baseline</th>
                <th>Outcome</th>
                <th>vs baseline</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((f) => {
                const c = forecastCells(f)
                return (
                  <tr key={f.id}>
                    <td className="mono" title={`issued ${formatDateTime(f.issuedUtc)} IST${f.scoredUtc ? ` · scored ${formatDateTime(f.scoredUtc)} IST` : ''}`}>
                      {f.sessionDate}
                    </td>
                    <td>{f.underlying}</td>
                    <td>{targetLabel(f.target)}</td>
                    <td className="mono" title={modelTitle(f.modelKey, f.modelVersion)}>
                      {f.modelKey}
                    </td>
                    <td className="r mono">{c.forecast}</td>
                    <td className="r mono muted">{c.baseline}</td>
                    <td>{c.outcome === '—' ? <span className="faint">{f.outcome ? '—' : 'not scored'}</span> : c.outcome}</td>
                    <td title={c.lossTitle}>
                      {c.beat == null ? (
                        <span className="faint">—</span>
                      ) : c.beat ? (
                        <span className="pos">beat</span>
                      ) : (
                        <span className="muted">behind</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        {rows.length > HISTORY_CAP && (
          <p className="small-note muted">
            Showing the newest {HISTORY_CAP} of {rows.length.toLocaleString('en-IN')}. Narrow by target or index to see
            further back.
          </p>
        )}
      </>
    )
  }

  return (
    <Panel
      title={
        <span className="chip-row">
          Past forecasts and what happened
          {rows && rows.length > 0 && <span className="faint">{rows.length.toLocaleString('en-IN')}</span>}
        </span>
      }
      actions={
        <div className="chip-row">
          <Seg
            label="Target"
            value={target}
            options={[{ key: '' as const, label: 'All targets' }, ...TARGETS.map((t) => ({ key: t, label: TARGET_META[t].label }))]}
            onChange={setTarget}
          />
          <Seg
            label="Index"
            value={underlying}
            options={[{ key: '', label: 'All indices' }, ...UNDERLYINGS.map((u) => ({ key: u as string, label: u as string }))]}
            onChange={setUnderlying}
          />
          <Seg label="Period" value={periodKey} options={PERIODS} onChange={setPeriodKey} />
        </div>
      }
    >
      {body}
      <p className="small-note muted">
        Newest session first. "Beat" means a lower loss than the baseline on that one session — a single row is noise;
        the scoreboard adds them up. Hover a row's cells for the losses and times (IST).
      </p>
    </Panel>
  )
}

// ---------------------------------------------------------------------------
// How this works
// ---------------------------------------------------------------------------

const TARGET_MODELS: Record<ForecastTarget, string> = {
  range: 'range.har, range.har-vix',
  trend: 'trend.logit',
  direction: 'direction.logit',
}

function HowSection() {
  return (
    <div className="an-how">
      <Panel title="The four rules that make it proof">
        <ol className="an-rules">
          <li>
            <b>Written before, scored after.</b> The API stamps each forecast with its own clock and refuses one for a
            session that has already opened ({SCHEDULE.open} IST). The outcome can be written only after that
            session's close. Nobody can back-date a forecast or change it afterwards.
          </li>
          <li>
            <b>Probabilities, not verdicts.</b> "38% chance of a trend day", "80% chance the range is between 0.7% and
            1.5%". The scoreboard checks calibration: of the times a model said 30–40%, did it happen 30–40% of the
            time?
          </li>
          <li>
            <b>Always against a baseline.</b> Every forecast carries the baseline's forecast for the same session — a
            20-session average range, or a trailing base rate. A model that cannot beat it is not useful, however
            clever.
          </li>
          <li>
            <b>A fixed pass mark, set before the data.</b> <i>Proven</i> needs at least {PROOF.provenAt} live, scored
            forecasts that beat the baseline with the low end of a {PROOF.confidencePct}% bootstrap confidence interval
            above zero. <i>Retired</i> is when, after {PROOF.retiredAt}, the high end is below zero. The walk-forward
            backtest is shown, but it never makes a model proven — only live forecasts do.
          </li>
        </ol>
      </Panel>

      <p className="an-control">
        Direction is kept as a control: if it stays at the baseline, that is the honest answer.
        <span className="an-control__more">
          The desk's research found nothing that predicts intraday direction, so if it ever beats the baseline, the
          first thing to look for is a leak.
        </span>
      </p>

      <Panel title="What is forecast, and against what">
        <div className="tablewrap">
          <table className="table an-targets">
            <thead>
              <tr>
                <th>Target</th>
                <th>What</th>
                <th>Models</th>
                <th>Baseline</th>
                <th>Loss (lower is better)</th>
              </tr>
            </thead>
            <tbody>
              {TARGETS.map((t) => (
                <tr key={t}>
                  <td>{TARGET_META[t].label}</td>
                  <td>{TARGET_META[t].what}</td>
                  <td className="mono">{TARGET_MODELS[t]}</td>
                  <td>{TARGET_META[t].baseline}</td>
                  <td className="mono">{TARGET_META[t].loss}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="small-note muted">
          For NIFTY, BANKNIFTY and SENSEX, on sessions of 09:15–15:30 IST. The range is also given as three buckets —
          quiet, normal and wild — whose edges are the index's terciles in the training data (for NIFTY, e.g.{' '}
          {bucketEdgesText([0.75, 1.25])}), so each bucket's baseline is about a third. Two weeks of strategy research
          found no edge in predicting intraday direction; volatility and regime are what research does find
          predictable, so version 1 forecasts those.
        </p>
      </Panel>

      <div className="an-how__grid">
        <Panel title="Reading the scoreboard">
          <dl className="an-defs">
            <div>
              <dt>Skill</dt>
              <dd>1 − model loss ÷ baseline loss. "12% better" means its average loss is 12% below the baseline's.</dd>
            </div>
            <div>
              <dt>95% interval</dt>
              <dd>
                A bootstrap ({PROOF.resamples.toLocaleString('en-IN')} resamples of whole sessions, fixed seed) of the
                mean of baseline loss − model loss, shown rescaled to skill. If it crosses zero, the model could be no
                better than the baseline.
              </dd>
            </div>
            <div>
              <dt>Coverage</dt>
              <dd>How often the actual range fell inside the 80% band. Near 80% is right; far from it, the band is too narrow or too wide.</dd>
            </div>
            <div>
              <dt>Calibration</dt>
              <dd>Every stated probability, in tenths: what was said against how often it happened.</dd>
            </div>
          </dl>
        </Panel>
        <Panel title="Status">
          <dl className="an-defs">
            {(Object.keys(STATUS_META) as Array<keyof typeof STATUS_META>).map((s) => (
              <div key={s}>
                <dt>
                  <Badge tone={STATUS_META[s].tone}>{STATUS_META[s].label}</Badge>
                </dt>
                <dd>{STATUS_META[s].meaning}</dd>
              </div>
            ))}
          </dl>
        </Panel>
        <Panel title="When">
          <dl className="an-defs">
            <div>
              <dt className="mono">{SCHEDULE.issue} IST</dt>
              <dd>Every NSE trading day, the desk issues the day's forecasts, before the {SCHEDULE.open} open.</dd>
            </div>
            <div>
              <dt className="mono">{SCHEDULE.score} IST</dt>
              <dd>
                After the {SCHEDULE.close} close, each forecast is scored against the session — from the live 1-minute
                bars, so the close is the last trade at 15:29, not the exchange's official close. Missed scoring
                catches up when the API starts.
              </dd>
            </div>
            <div>
              <dt>By hand</dt>
              <dd>After a model change, the backtest refits the models on history and registers the new version.</dd>
            </div>
          </dl>
        </Panel>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------

export function AnalysisPage() {
  const [params, setParams] = useSearchParams()
  const section = sectionFrom(params.get('section'))
  const nowMs = useNow(30_000)
  const models = useForecastModels()
  const board = useForecastScoreboard()
  const groups = useMemo(() => groupScoreboard(board.data, models.data), [board.data, models.data])
  const answer = board.data ? deskAnswer(groups) : null
  const go = (key: SectionKey) => setParams(key === 'today' ? {} : { section: key }, { replace: true })

  return (
    <div className="page an">
      <header className="page__header">
        <div>
          <h1 className="page__title">Forecasts</h1>
          <p className="page__subtitle">
            Is there anything about the market this desk can predict better than a simple baseline — and can it prove
            it? Written before the open, scored after the close, judged only on live forecasts.
          </p>
        </div>
      </header>

      {answer && (
        <p className={`an-answer an-answer--${answer.tone}`}>
          <span className="an-answer__text">{answer.text}</span>
          {section !== 'how' && (
            <button type="button" className="an-answer__link" onClick={() => go('how')}>
              The proof rules
            </button>
          )}
        </p>
      )}

      <div className="oc-tabs" role="tablist" aria-label="Section">
        {SECTIONS.map((s) => (
          <button
            key={s.key}
            type="button"
            role="tab"
            aria-selected={section === s.key}
            className={`oc-tab ${section === s.key ? 'oc-tab--on' : ''}`}
            onClick={() => go(s.key)}
          >
            {s.label}
          </button>
        ))}
      </div>

      <div role="tabpanel" className="an-body">
        {section === 'today' && (
          <TodaySection
            groups={groups}
            modelsKnown={models.data ? models.data.length > 0 : null}
            nowMs={nowMs}
          />
        )}
        {section === 'scoreboard' && (
          <ScoreboardSection groups={groups} board={board} modelsError={models.isError ? models.error : null} />
        )}
        {section === 'history' && <HistorySection nowMs={nowMs} />}
        {section === 'how' && <HowSection />}
      </div>
    </div>
  )
}
