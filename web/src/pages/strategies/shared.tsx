/**
 * Strategies module — pieces shared by the Live runner, Library and Overview
 * screens: the catalogue card, the launch dialog (underlying → lots →
 * optional stop-loss/target → advanced params), the readiness strip and the
 * small P&L / category primitives.
 *
 * Launching is deliberately dialog-shaped: the user must see which
 * underlyings actually have option contracts loaded (with lot size and strike
 * step) before choosing one — the house rule is "show what data exists before
 * asking the user to pick".
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  useBrokerSession,
  useFnoUnderlyings,
  useIngestorProcessStatus,
  useIngestorStatuses,
  useMarketSession,
  useStartStrategy,
} from '../../lib/queries'
import {
  formatDateTime,
  formatInrSigned,
  formatInrWhole,
  formatNumber,
  formatTime,
  pnlClass,
} from '../../lib/format'
import { formatResolution } from '../../lib/symbols'
import { EMPTY_RISK_DRAFT, RISK_UPDATED_TYPE, activityText, parseRiskDraft } from '../../lib/risk'
import type { RiskDraft, RiskDraftField } from '../../lib/risk'
import { formatPnlMove } from '../../lib/positions'
import type { PositionValues } from '../../lib/positions'
import {
  contractRequirementSummary,
  contractRequirementsOf,
  describeRequirement,
  describeRequirementDistance,
  parseStrikeParams,
  parseStrikeValue,
  requirementLabel,
  seedStrikeParams,
  strikeParamKeys,
  strikeParamLabel,
  strikeParams,
  strikeValueInvalid,
} from '../../lib/contracts'
import { activeUnderlyings } from '../../lib/strategyList'
import { Badge, InlineError, Loading } from '../../components/ui'
import { RiskRulesForm } from '../../components/RiskRulesForm'
import { IconChevronDown, IconChevronRight, IconPlay, IconX } from '../../components/icons'
import type {
  FnoUnderlying,
  LiveActivity,
  StartStrategyRequest,
  StartStrategyResponse,
  StrategyContractRequirement,
  StrategyListItem,
} from '../../lib/types'

/* --------------------------------------------------------------- primitives */

const CATEGORY_TONES: Record<string, 'pos' | 'neg' | 'warn' | 'neutral' | 'accent'> = {
  bullish: 'pos',
  bearish: 'neg',
  neutral: 'neutral',
  directional: 'accent',
  adjustment: 'warn',
  example: 'neutral',
}

export function CategoryBadge({ category }: { category: string | null | undefined }) {
  if (!category) return null
  return <Badge tone={CATEGORY_TONES[category.toLowerCase()] ?? 'neutral'}>{category}</Badge>
}

/** Signed rupee P&L, coloured by sign. */
export function PnlValue({
  value,
  className,
}: {
  value: number | null | undefined
  className?: string
}) {
  return (
    <span className={`mono ${pnlClass(value)} ${className ?? ''}`}>{formatInrSigned(value)}</span>
  )
}

/**
 * "Value" cell of a position row: entry value, and for an open row a muted
 * "now <current value>" second line.
 */
export function PositionValueCell({ values, open }: { values: PositionValues; open: boolean }) {
  return (
    <td className="r mono">
      {values.entryValue != null ? formatInrWhole(values.entryValue) : <span className="muted">—</span>}
      {open && values.currentValue != null && (
        <span className="cell-sub">now {formatInrWhole(values.currentValue)}</span>
      )}
    </td>
  )
}

/** "P&L" cell of a position row: signed rupees plus a muted "+6.2 pts · +0.7%" line. */
export function PositionPnlCell({ pnl, values }: { pnl: number; values: PositionValues }) {
  const move = formatPnlMove(values)
  return (
    <td className="r">
      <PnlValue value={pnl} />
      {move && <span className="cell-sub">{move}</span>}
    </td>
  )
}

/** "09 Sep" from a yyyy-MM-dd or ISO date. */
export function formatDay(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })
}

/* ---------------------------------------------------------------- readiness */

export type FeedState = 'live' | 'degraded' | 'stopped'

/** The three things a runner needs — none of them blocks a start, all warn. */
export function useReadiness() {
  const session = useMarketSession()
  const broker = useBrokerSession()
  const ingestors = useIngestorStatuses()
  const process = useIngestorProcessStatus()

  const feeds = ingestors.data ?? []
  const healthy = feeds.filter((f) => f.isHealthy).length
  const processRunning = process.data?.isRunning ?? false

  let feed: FeedState | null = null
  if (ingestors.data || process.data) {
    if (feeds.length > 0 && healthy === feeds.length) feed = 'live'
    else if (healthy > 0 || processRunning) feed = 'degraded'
    else feed = 'stopped'
  }

  return {
    marketOpen: session.data?.isMarketOpen ?? null,
    nextMarketOpenUtc: session.data?.nextMarketOpenUtc ?? null,
    brokerLinked: broker.data?.isAuthenticated ?? null,
    feed,
    feedHealthy: healthy,
    feedTotal: feeds.length,
  }
}

function ReadinessPill({
  tone,
  label,
  title,
  to,
}: {
  tone: 'pos' | 'neg' | 'warn' | 'live' | 'idle'
  label: string
  title?: string
  to?: string
}) {
  const className = `pill ${tone === 'idle' ? '' : `pill--${tone}`}`
  const body = (
    <>
      <span className="pill__dot" aria-hidden="true" />
      {label}
    </>
  )
  if (to) {
    return (
      <Link className={className} to={to} title={title}>
        {body}
      </Link>
    )
  }
  return (
    <span className={className} title={title}>
      {body}
    </span>
  )
}

/** Market · FYERS · Feed pills; the fixable ones link to their page. */
export function ReadinessStrip() {
  const r = useReadiness()
  return (
    <div className="readiness" aria-label="Readiness">
      <ReadinessPill
        tone={r.marketOpen == null ? 'idle' : r.marketOpen ? 'pos' : 'idle'}
        label={r.marketOpen == null ? 'Market …' : r.marketOpen ? 'Market open' : 'Market closed'}
        title={
          r.marketOpen === false && r.nextMarketOpenUtc
            ? `Next open: ${new Date(r.nextMarketOpenUtc).toLocaleString('en-IN')}`
            : 'NSE session'
        }
      />
      <ReadinessPill
        tone={r.brokerLinked == null ? 'idle' : r.brokerLinked ? 'pos' : 'neg'}
        label={r.brokerLinked == null ? 'FYERS …' : r.brokerLinked ? 'FYERS linked' : 'FYERS not linked'}
        title="Broker session — click to manage"
        to="/admin/broker"
      />
      <ReadinessPill
        tone={
          r.feed === 'live' ? 'live' : r.feed === 'degraded' ? 'warn' : r.feed === 'stopped' ? 'neg' : 'idle'
        }
        label={
          r.feed === 'live'
            ? `Feed live (${r.feedTotal})`
            : r.feed === 'degraded'
              ? `Feed degraded (${r.feedHealthy}/${r.feedTotal})`
              : r.feed === 'stopped'
                ? 'Feed stopped'
                : 'Feed …'
        }
        title="Live ingestor — click to manage"
        to="/admin/data/live"
      />
    </div>
  )
}

/* ----------------------------------------------------------- strategy card */

export function StrategyCard({
  strategy,
  onStart,
  actionLabel = 'Start…',
  allowWhileActive = false,
}: {
  strategy: StrategyListItem
  onStart: (strategy: StrategyListItem) => void
  /** Button text; the Live runner starts, the Backtesting module replays. */
  actionLabel?: string
  /**
   * The action is not a live start (a backtest): keep `actionLabel` while the
   * strategy runs instead of offering "another underlying".
   */
  allowWhileActive?: boolean
}) {
  const s = strategy
  const lots = Math.max(1, s.defaultLots || 1)
  const on = activeUnderlyings(s)
  const requirements = contractRequirementsOf(s.contractRequirements)
  // A live run never blocks the button: the same strategy may be started on a
  // second underlying, and the launch dialog greys out the ones already taken.
  const label = s.isActive && !allowWhileActive ? 'Start on another underlying…' : actionLabel
  return (
    <article className={`strategy-card ${s.isActive ? 'strategy-card--running' : ''}`}>
      <div className="strategy-card__head">
        <span className="strategy-card__name">{s.name}</span>
        <CategoryBadge category={s.category} />
        {s.isActive && (
          <Badge tone="pos">
            running{on.length > 0 ? ` · ${on.join(', ')}` : ''}
          </Badge>
        )}
      </div>
      <p className="strategy-card__desc" title={s.description || undefined}>
        {s.description || 'No description provided by the strategy.'}
      </p>
      {s.supportedUnderlyings.length > 0 && (
        <div className="chip-row">
          {s.supportedUnderlyings.map((u) => (
            <span key={u} className="badge badge--neutral mono">
              {u}
            </span>
          ))}
        </div>
      )}
      {s.legsSummary && <div className="strategy-card__legs">{s.legsSummary}</div>}
      {requirements.length > 0 && (
        <div className="chip-row" aria-label="Contracts">
          {requirements.map((r) => (
            <span key={r.key} className="badge badge--neutral mono" title={describeRequirement(r)}>
              {r.key}
            </span>
          ))}
        </div>
      )}
      <div className="strategy-card__foot">
        <span className="strategy-card__hint">
          default {lots} {lots === 1 ? 'lot' : 'lots'}
          {s.instrumentKind ? ` · ${s.instrumentKind}` : ''}
        </span>
        <button
          type="button"
          className="btn btn--sm btn--primary"
          onClick={() => onStart(s)}
          title={
            on.length > 0 && !allowWhileActive
              ? `Already running on ${on.join(', ')} — pick a different underlying`
              : undefined
          }
        >
          <IconPlay style={{ width: 13, height: 13 }} /> {label}
        </button>
      </div>
    </article>
  )
}

/* ------------------------------------------------------- parameter editing */

export interface ParamRow {
  key: string
  value: string
}

/**
 * Parameter rows from a JSON object string. `omit` drops keys the dialog
 * shows as dedicated fields (a backtest's parametersJson also carries lots,
 * stop_loss, … which must not appear twice).
 */
export function parseParamDefaults(json: string, omit?: ReadonlySet<string>): ParamRow[] {
  try {
    const obj = JSON.parse(json || '{}') as Record<string, unknown>
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return []
    return Object.entries(obj)
      .filter(([key]) => !omit || !omit.has(key))
      .map(([key, value]) => ({
        key,
        value: typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value),
      }))
  } catch {
    return []
  }
}

/** "70" -> 70, "true" -> true, anything else stays a string (as on DeployPage). */
export function coerceParam(value: string): unknown {
  const trimmed = value.trim()
  if (trimmed === 'true') return true
  if (trimmed === 'false') return false
  if (trimmed !== '' && !Number.isNaN(Number(trimmed))) return Number(trimmed)
  return trimmed
}

/** Non-empty rows as the `parameters` object the start endpoints accept. */
export function paramsToObject(rows: ParamRow[]): Record<string, unknown> | null {
  const entries = rows
    .filter((p) => p.key.trim() !== '')
    .map((p) => [p.key.trim(), coerceParam(p.value)] as const)
  return entries.length > 0 ? Object.fromEntries(entries) : null
}

export function ParamGrid({ rows, onChange }: { rows: ParamRow[]; onChange: (rows: ParamRow[]) => void }) {
  return (
    <div>
      {rows.length === 0 && (
        <p className="field__help" style={{ margin: '0 0 8px' }}>
          This strategy declares no default parameters — add any it understands.
        </p>
      )}
      <div className="param-grid">
        {rows.map((p, i) => (
          <div key={i} className="param-row">
            <input
              className="field__input field__input--sm"
              placeholder="parameter"
              value={p.key}
              aria-label={`Parameter ${i + 1} name`}
              onChange={(e) =>
                onChange(rows.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))
              }
            />
            <input
              className="field__input field__input--sm"
              placeholder="value"
              value={p.value}
              aria-label={`Parameter ${i + 1} value`}
              onChange={(e) =>
                onChange(rows.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))
              }
            />
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={() => onChange(rows.filter((_, j) => j !== i))}
              aria-label={`Remove parameter ${i + 1}`}
            >
              <IconX style={{ width: 12, height: 12 }} />
            </button>
          </div>
        ))}
      </div>
      <button
        type="button"
        className="btn btn--ghost btn--sm"
        onClick={() => onChange([...rows, { key: '', value: '' }])}
      >
        + Add parameter
      </button>
    </div>
  )
}

/**
 * Free-form parameter rows merged with the strike-selection values (which win,
 * being the dedicated fields for those keys). Null when nothing is set, which
 * is what the start endpoints expect for "no parameters".
 */
export function mergeParams(
  rows: ParamRow[],
  strike: Record<string, number>,
): Record<string, unknown> | null {
  const merged = { ...(paramsToObject(rows) ?? {}), ...strike }
  return Object.keys(merged).length > 0 ? merged : null
}

/* -------------------------------------------------------- strike selection */

/**
 * Everything a dialog needs to run the Strike selection section: the strategy's
 * requirements, the parameters that move them, the text state seeded from the
 * defaults, and the keys the free-form parameter grid must not repeat.
 *
 * `parametersJson` is the strategy's defaults, or an earlier run's parameters
 * when the dialog is a "Run again".
 */
export function useStrikeSelection(strategy: StrategyListItem, parametersJson?: string) {
  const requirements = useMemo(
    () => contractRequirementsOf(strategy.contractRequirements),
    [strategy.contractRequirements],
  )
  const params = useMemo(() => strikeParams(requirements), [requirements])
  const seedJson = parametersJson ?? strategy.defaultParametersJson
  // Seeded once: a poll that refreshes the catalogue entry must not overwrite
  // what the user is typing.
  const [values, setValues] = useState<Record<string, string>>(() =>
    seedStrikeParams(strikeParams(contractRequirementsOf(strategy.contractRequirements)), seedJson),
  )
  const omitKeys = useMemo(() => strikeParamKeys(requirements), [requirements])
  return { requirements, params, values, setValues, omitKeys }
}

/**
 * The strikes a run will trade: one line per contract the strategy asks for,
 * and an input for each distance parameter it exposes. The lines are written
 * against the chosen underlying's strike grid, so "+2 strikes" also reads as
 * "+200 pts on BANKNIFTY" — the house rule is to show what a choice means
 * before asking for it.
 */
export function StrikeSelection({
  requirements,
  underlying,
  values,
  onChange,
  idPrefix,
  invalidParam = null,
  disabled = false,
}: {
  requirements: StrategyContractRequirement[]
  /** The chosen underlying — its strikeStep turns steps into points. */
  underlying: FnoUnderlying | null
  values: Record<string, string>
  onChange: (next: Record<string, string>) => void
  idPrefix: string
  /** The parameter a submit-time parse rejected. */
  invalidParam?: string | null
  disabled?: boolean
}) {
  const params = useMemo(() => strikeParams(requirements), [requirements])
  const step = underlying && underlying.strikeStep > 0 ? underlying.strikeStep : null

  return (
    <div className="strike-sel">
      <ul className="strike-sel__list">
        {requirements.map((req) => (
          <li key={req.key} className="strike-sel__item">
            <span className="strike-sel__key mono">{req.key}</span>
            <span className="strike-sel__what">{requirementLabel(req)}</span>
            <span className="strike-sel__dist">
              {describeRequirementDistance(req, values, underlying)}
            </span>
            {req.optional && (
              <span className="faint" title="The strategy runs even when this contract cannot be resolved">
                optional
              </span>
            )}
          </li>
        ))}
      </ul>
      {params.length > 0 && (
        <div className="strike-sel__params">
          {params.map((p) => {
            const id = `${idPrefix}-${p.name}`
            const raw = values[p.name] ?? ''
            const typed = parseStrikeValue(raw)
            const set = typed != null && !Number.isNaN(typed) ? typed : null
            const equivalent =
              step != null && set != null && underlying
                ? p.points
                  ? `≈ ${formatNumber(Math.round((set / step) * 100) / 100)} strikes on ${underlying.underlying}`
                  : `= ${formatNumber(set * step)} pts on ${underlying.underlying} (step ${formatNumber(step)})`
                : null
            return (
              <div key={p.name} className="field">
                <label className="field__label" htmlFor={id}>
                  {strikeParamLabel(p.name)}
                </label>
                <input
                  id={id}
                  className="field__input"
                  type="number"
                  min={0}
                  step={p.points ? 50 : 1}
                  inputMode="decimal"
                  placeholder="strategy default"
                  value={raw}
                  disabled={disabled}
                  aria-invalid={invalidParam === p.name || strikeValueInvalid(raw) || undefined}
                  onChange={(e) => onChange({ ...values, [p.name]: e.target.value })}
                />
                <span className="field__help">
                  {p.points ? 'points' : 'strikes'} away from ATM · moves {p.keys.join(', ')}
                  {equivalent ? ` · ${equivalent}` : ''}
                </span>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

/* ------------------------------------------------------------ launch dialog */

export function UnderlyingPicker({
  list,
  supported,
  running,
  value,
  onChange,
}: {
  list: FnoUnderlying[]
  supported: Set<string>
  /** Upper-cased underlyings this strategy is already live on — not startable again. */
  running?: ReadonlySet<string>
  value: string | null
  onChange: (underlying: string) => void
}) {
  return (
    <div className="pick-list" role="radiogroup" aria-label="Underlying">
      {list.map((u) => {
        const key = u.underlying.toUpperCase()
        const ok = supported.has(key)
        const taken = ok && (running?.has(key) ?? false)
        const active = value === u.underlying
        return (
          <button
            key={u.underlying}
            type="button"
            role="radio"
            aria-checked={active}
            className={`pick-row ${active ? 'is-active' : ''}`}
            disabled={!ok || taken}
            onClick={() => onChange(u.underlying)}
            title={
              !ok
                ? 'Not supported by this strategy'
                : taken
                  ? 'Already running on this underlying — stop that run first'
                  : u.spotSymbol
            }
          >
            <span className="pick-row__name">
              {u.underlying} <span className="faint" style={{ fontWeight: 500 }}>{u.exchange}</span>
            </span>
            <span className="pick-row__tag">
              {!ok
                ? 'not supported by this strategy'
                : taken
                  ? 'already running'
                  : `${formatNumber(u.optionContracts)} contracts`}
            </span>
            <span className="pick-row__meta">
              <span>next expiry {formatDay(u.nextExpiry)}</span>
              <span>
                lot {u.lotSize}
                {u.lotSizeSource !== 'master' ? ` (${u.lotSizeSource})` : ''}
              </span>
              <span>step {u.strikeStep}</span>
              <span>{u.expiries.length} expiries</span>
            </span>
          </button>
        )
      })}
    </div>
  )
}

/** Under the launch picker: why rows are greyed out, if any are. */
function PickerHelp({
  strategy,
  anySupported,
  anyStartable,
}: {
  strategy: StrategyListItem
  anySupported: boolean
  anyStartable: boolean
}) {
  const on = activeUnderlyings(strategy)
  if (!anySupported) {
    return (
      <span className="field__help warn">None of the loaded underlyings is supported by this strategy.</span>
    )
  }
  if (on.length === 0) return null
  return (
    <span className={`field__help ${anyStartable ? '' : 'warn'}`}>
      {strategy.name} is already running on {on.join(', ')}
      {anyStartable
        ? ' — those rows are greyed out; pick another underlying.'
        : ' — every supported underlying is taken; stop a run first.'}
    </span>
  )
}

/** Left column of the launch/backtest dialogs: what the strategy is. */
export function StrategyAside({ strategy, titleId }: { strategy: StrategyListItem; titleId: string }) {
  const requirements = contractRequirementsOf(strategy.contractRequirements)
  return (
    <aside className="modal__aside">
      <div>
        <h2 className="modal__title" id={titleId}>
          {strategy.name}
        </h2>
        <div className="chip-row" style={{ marginTop: 6 }}>
          <CategoryBadge category={strategy.category} />
          {strategy.instrumentKind && <Badge tone="neutral">{strategy.instrumentKind}</Badge>}
        </div>
      </div>
      <p className="card__muted" style={{ fontSize: 12.5, lineHeight: 1.5 }}>
        {strategy.description || 'No description provided by the strategy.'}
      </p>
      <dl className="detail-list">
        <div>
          <dt>Legs</dt>
          <dd className="mono">{strategy.legsSummary || '—'}</dd>
        </div>
        {requirements.length > 0 && (
          <div>
            <dt>Contracts</dt>
            <dd className="mono" style={{ whiteSpace: 'normal' }}>
              {contractRequirementSummary(requirements)}
            </dd>
          </div>
        )}
        <div>
          <dt>Data needs</dt>
          <dd>
            {strategy.dataRequirements.length === 0
              ? '—'
              : strategy.dataRequirements
                  .map((d) => `${d.symbolType} @ ${formatResolution(d.resolution)}`)
                  .join(', ')}
          </dd>
        </div>
        <div>
          <dt>Supported underlyings</dt>
          <dd className="mono">
            {strategy.supportedUnderlyings.length === 0 ? '—' : strategy.supportedUnderlyings.join(', ')}
          </dd>
        </div>
        <div>
          <dt>Source</dt>
          <dd className="mono">{strategy.sourceFile || '—'}</dd>
        </div>
      </dl>
    </aside>
  )
}

/** Escape closes the dialog; the card takes focus on mount. */
export function useDialogChrome(onClose: () => void) {
  const cardRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])
  useEffect(() => {
    cardRef.current?.focus()
  }, [])
  return cardRef
}

/* ------------------------------------------------ run-card building blocks */

export function activityTone(type: string): 'pos' | 'neg' | 'warn' | 'neutral' | 'accent' {
  const t = type.toUpperCase()
  if (t === 'RUN_STOPPED' || t === 'SKIPPED' || t === 'SKIP') return 'warn'
  if (t === RISK_UPDATED_TYPE) return 'accent'
  if (t.startsWith('OPEN') || t === 'BUY') return 'pos'
  if (t.startsWith('CLOSE') || t === 'SELL') return 'neg'
  if (t.startsWith('ADJUST')) return 'accent'
  return 'neutral'
}

/** Badge text of a signal type: RISK_UPDATED reads as "risk", the rest as-is. */
function activityLabel(type: string): string {
  return type.toUpperCase() === RISK_UPDATED_TYPE ? 'RISK' : type
}

/** Signals of a run, newest first. `showDate` for multi-day (backtest) runs. */
export function ActivityList({ items, showDate }: { items: LiveActivity[]; showDate?: boolean }) {
  if (items.length === 0) return <p className="empty">No signals recorded for this run yet.</p>
  return (
    <ul className={`activity ${showDate ? 'activity--dated' : ''}`}>
      {items.map((a, i) => {
        // RISK_UPDATED rows are rendered from their metadata ("Risk rules
        // updated by admin: …"); the guard's leg/group CLOSE_GROUP reasons
        // and every other row show the API's text as it came.
        const text = activityText(a)
        return (
          <li key={`${a.atUtc}-${i}`} className="activity__item">
            <span className="activity__time">{showDate ? formatDateTime(a.atUtc) : formatTime(a.atUtc)}</span>
            <Badge tone={activityTone(a.type)}>{activityLabel(a.type)}</Badge>
            <span className="activity__text" title={a.groupId ? `${text} · group ${a.groupId}` : text}>
              {text}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

/** Collapsible sub-section of a run card (activity, runner output, …). */
export function Disclosure({
  label,
  open,
  onToggle,
  children,
}: {
  label: string
  open: boolean
  onToggle: () => void
  children: ReactNode
}) {
  return (
    <div className="run-card__section">
      <button type="button" className="disclosure__btn" onClick={onToggle} aria-expanded={open}>
        {open ? <IconChevronDown /> : <IconChevronRight />}
        {label}
      </button>
      {open && <div className="disclosure__body">{children}</div>}
    </div>
  )
}

/** Terminal-styled log box that follows its tail as lines arrive. */
export function ConsoleOutput({
  lines,
  placeholder,
  title = 'Runner process output',
}: {
  lines: string[]
  placeholder: string
  title?: string
}) {
  const bodyRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = bodyRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [lines.length])
  return (
    <div className="console">
      <div className="console__bar">
        <span className="console__dot console__dot--r" />
        <span className="console__dot console__dot--y" />
        <span className="console__dot console__dot--g" />
        <span className="console__title">{title}</span>
      </div>
      <div className={`console__body ${lines.length === 0 ? 'faint' : ''}`} ref={bodyRef}>
        {lines.length === 0 ? placeholder : lines.map((line, i) => <div key={i}>{line}</div>)}
      </div>
    </div>
  )
}

/**
 * Modal launcher. Closes on Escape, on backdrop click and on Cancel; on a
 * successful start it reports the response and closes.
 */
export function LaunchDialog({
  strategy,
  onClose,
  onStarted,
}: {
  strategy: StrategyListItem
  onClose: () => void
  onStarted?: (response: StartStrategyResponse) => void
}) {
  const underlyings = useFnoUnderlyings()
  const start = useStartStrategy()
  const readiness = useReadiness()
  const cardRef = useDialogChrome(onClose)

  const supported = useMemo(
    () => new Set(strategy.supportedUnderlyings.map((u) => u.toUpperCase())),
    [strategy.supportedUnderlyings],
  )
  // Underlyings this strategy is already live on: the API answers 409 for
  // them, so the rows are greyed out before the user gets that far.
  const running = useMemo(
    () => new Set(strategy.activeRuns.map((r) => r.underlying.toUpperCase())),
    [strategy.activeRuns],
  )

  const [underlying, setUnderlying] = useState<string | null>(null)
  const [lots, setLots] = useState(String(Math.max(1, strategy.defaultLots || 1)))
  const [risk, setRisk] = useState<RiskDraft>(EMPTY_RISK_DRAFT)
  const [riskField, setRiskField] = useState<RiskDraftField | null>(null)
  // Bumped on every failed submit so the form re-focuses the same field on a repeat.
  const [riskNonce, setRiskNonce] = useState(0)
  const [capital, setCapital] = useState(String(1_000_000))
  const [advanced, setAdvanced] = useState(false)
  // Strike distances get their own inputs, so they are kept out of the
  // free-form parameter grid rather than editable in two places.
  const strike = useStrikeSelection(strategy)
  const [strikeField, setStrikeField] = useState<string | null>(null)
  const [params, setParams] = useState<ParamRow[]>(() =>
    parseParamDefaults(strategy.defaultParametersJson, strike.omitKeys),
  )
  const [validation, setValidation] = useState<string | null>(null)

  const list = underlyings.data ?? []
  const firstSupported = list.find((u) => supported.has(u.underlying.toUpperCase())) ?? null
  const firstStartable =
    list.find((u) => {
      const key = u.underlying.toUpperCase()
      return supported.has(key) && !running.has(key)
    }) ?? null
  const chosen = list.find((u) => u.underlying === underlying) ?? null
  const chosenTaken = chosen != null && running.has(chosen.underlying.toUpperCase())

  // Default to the first supported underlying that is not already live once
  // the list is in.
  useEffect(() => {
    if (underlying == null && firstStartable) setUnderlying(firstStartable.underlying)
  }, [underlying, firstStartable])

  const lotsNum = Number(lots)
  const units = chosen && Number.isInteger(lotsNum) && lotsNum > 0 ? lotsNum * chosen.lotSize : null

  function submit() {
    setValidation(null)
    setRiskField(null)
    setStrikeField(null)
    if (!chosen) {
      setValidation('Pick an underlying — the strategy must know what it trades.')
      return
    }
    if (chosenTaken) {
      setValidation(
        `${strategy.name} is already running on ${chosen.underlying} — stop that run or pick another underlying.`,
      )
      return
    }
    if (!Number.isInteger(lotsNum) || lotsNum < 1) {
      setValidation('Lots must be a whole number of at least 1.')
      return
    }
    const strikes = parseStrikeParams(strike.params, strike.values)
    if (strikes.values === null) {
      setStrikeField(strikes.param)
      setValidation(strikes.error)
      return
    }
    const parsed = parseRiskDraft(risk)
    if (parsed.rules === null) {
      setRiskField(parsed.field)
      setRiskNonce((n) => n + 1)
      setValidation(parsed.error)
      return
    }
    const cap = Number(capital)
    if (!(cap > 0)) {
      setValidation('Capital must be a positive amount.')
      return
    }
    // The legacy stopLoss/target fields mirror the overall level so an API
    // build from before the three-level rules still applies them.
    const body: StartStrategyRequest = {
      underlying: chosen.underlying,
      lots: lotsNum,
      stopLoss: parsed.rules.overall?.stopLoss ?? null,
      target: parsed.rules.overall?.target ?? null,
      risk: parsed.rules,
      parameters: mergeParams(params, strikes.values),
      initialCapital: cap,
    }
    start.mutate(
      { id: strategy.id, body },
      {
        onSuccess: (response) => {
          onStarted?.(response)
          onClose()
        },
      },
    )
  }

  const notes: ReactNode[] = []
  if (readiness.marketOpen === false)
    notes.push(<span key="market">Market is closed: the runner will start and wait for ticks.</span>)
  if (readiness.feed && readiness.feed !== 'live')
    notes.push(
      <span key="feed">
        Live feed is not running — start it on <Link to="/admin/data/live">Data › Live feeds</Link>.
      </span>,
    )
  if (readiness.brokerLinked === false)
    notes.push(
      <span key="broker">
        FYERS is not linked — no ticks will arrive until the <Link to="/admin/broker">broker session</Link>{' '}
        is restored.
      </span>,
    )

  const canStart = !!chosen && !chosenTaken && !start.isPending

  return (
    <div
      className="modal"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose()
      }}
    >
      <div
        className="modal__card"
        role="dialog"
        aria-modal="true"
        aria-labelledby="launch-title"
        tabIndex={-1}
        ref={cardRef}
      >
        <StrategyAside strategy={strategy} titleId="launch-title" />

        <div className="modal__body">
          <div className="modal__head">
            <span className="section-title" style={{ margin: 0 }}>
              Launch on paper
            </span>
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={onClose}
              aria-label="Close"
              title="Close (Esc)"
            >
              <IconX style={{ width: 14, height: 14 }} />
            </button>
          </div>

          <div className="field">
            <span className="field__label">Underlying (required)</span>
            {underlyings.isPending ? (
              <Loading label="Loading F&O universe…" />
            ) : underlyings.isError && underlyings.data === undefined ? (
              <InlineError error={underlyings.error} />
            ) : underlyings.isError ? (
              // Same rule as QueryBoundary: a failed background refetch keeps the
              // last good list on screen instead of taking the picker away.
              <>
                <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
                  Refresh failed — showing the last loaded data.
                </p>
                <UnderlyingPicker
                  list={list}
                  supported={supported}
                  running={running}
                  value={underlying}
                  onChange={setUnderlying}
                />
                <PickerHelp
                  strategy={strategy}
                  anySupported={!!firstSupported}
                  anyStartable={!!firstStartable}
                />
              </>
            ) : list.length === 0 ? (
              <div className="alert alert--warn" role="status">
                <span>
                  No F&O contracts loaded — import the instrument master first on{' '}
                  <Link to="/admin/data/instruments">Data › Instruments & F&O</Link>.
                </span>
              </div>
            ) : (
              <>
                <UnderlyingPicker
                  list={list}
                  supported={supported}
                  running={running}
                  value={underlying}
                  onChange={setUnderlying}
                />
                <PickerHelp
                  strategy={strategy}
                  anySupported={!!firstSupported}
                  anyStartable={!!firstStartable}
                />
              </>
            )}
          </div>

          <div className="form-row">
            <div className="field">
              <label className="field__label" htmlFor="launch-lots">
                Lots
              </label>
              <input
                id="launch-lots"
                className="field__input"
                type="number"
                min={1}
                step={1}
                inputMode="numeric"
                value={lots}
                onChange={(e) => setLots(e.target.value)}
              />
              <span className="field__help">
                {chosen && units != null
                  ? `= ${formatNumber(units)} units (lot size ${chosen.lotSize})`
                  : 'whole lots of the chosen underlying'}
              </span>
            </div>
          </div>

          {strike.requirements.length > 0 && (
            <div className="field">
              <span className="field__label">
                Strike selection{chosen ? ` (on the ${chosen.underlying} grid, step ${chosen.strikeStep})` : ''}
              </span>
              <StrikeSelection
                requirements={strike.requirements}
                underlying={chosen}
                values={strike.values}
                onChange={(next) => {
                  strike.setValues(next)
                  setStrikeField(null)
                }}
                idPrefix="launch-strike"
                invalidParam={strikeField}
              />
            </div>
          )}

          <div className="field">
            <span className="field__label">Risk rules (all optional · editable while the run is live)</span>
            <RiskRulesForm
              value={risk}
              onChange={(next) => {
                setRisk(next)
                setRiskField(null)
              }}
              idPrefix="launch-risk"
              invalidField={riskField}
              invalidNonce={riskNonce}
            />
          </div>

          <div>
            <button
              type="button"
              className="disclosure__btn"
              onClick={() => setAdvanced((v) => !v)}
              aria-expanded={advanced}
            >
              {advanced ? <IconChevronDown /> : <IconChevronRight />}
              Advanced
              <span className="faint" style={{ fontWeight: 500 }}>
                · parameters and capital
              </span>
            </button>
            {advanced && (
              <div className="disclosure__body" style={{ display: 'grid', gap: 12 }}>
                <div className="field" style={{ maxWidth: 260 }}>
                  <label className="field__label" htmlFor="launch-capital">
                    Capital ({formatInrWhole(Number(capital) || 0)})
                  </label>
                  <input
                    id="launch-capital"
                    className="field__input"
                    type="number"
                    min={100000}
                    step={100000}
                    value={capital}
                    onChange={(e) => setCapital(e.target.value)}
                  />
                </div>
                <div className="field">
                  <span className="field__label">Parameters</span>
                  <ParamGrid rows={params} onChange={setParams} />
                </div>
              </div>
            )}
          </div>

          {notes.length > 0 && (
            <div className="alert alert--warn" role="status" style={{ flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
              {notes}
            </div>
          )}

          {validation && (
            <div className="alert alert--error" role="alert">
              {validation}
            </div>
          )}
          {start.isError && <InlineError error={start.error} />}

          <div className="modal__foot">
            <button type="button" className="btn btn--ghost" onClick={onClose}>
              Cancel
            </button>
            <button type="button" className="btn btn--pos" disabled={!canStart} onClick={submit}>
              <IconPlay style={{ width: 14, height: 14 }} />
              {start.isPending
                ? 'Starting…'
                : `Start on ${chosen?.underlying ?? '…'} (paper)`}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
