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
import { formatInrSigned, formatInrWhole, formatNumber, pnlClass } from '../../lib/format'
import { formatResolution } from '../../lib/symbols'
import { Badge, InlineError, Loading } from '../../components/ui'
import { IconChevronDown, IconChevronRight, IconPlay, IconX } from '../../components/icons'
import type {
  FnoUnderlying,
  StartStrategyRequest,
  StartStrategyResponse,
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
}: {
  strategy: StrategyListItem
  onStart: (strategy: StrategyListItem) => void
}) {
  const s = strategy
  const lots = Math.max(1, s.defaultLots || 1)
  return (
    <article className={`strategy-card ${s.isActive ? 'strategy-card--running' : ''}`}>
      <div className="strategy-card__head">
        <span className="strategy-card__name">{s.name}</span>
        <CategoryBadge category={s.category} />
        {s.isActive && (
          <Badge tone="pos">running{s.underlying ? ` · ${s.underlying}` : ''}</Badge>
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
      <div className="strategy-card__foot">
        <span className="strategy-card__hint">
          default {lots} {lots === 1 ? 'lot' : 'lots'}
          {s.instrumentKind ? ` · ${s.instrumentKind}` : ''}
        </span>
        <button
          type="button"
          className={`btn btn--sm ${s.isActive ? '' : 'btn--primary'}`}
          disabled={s.isActive}
          onClick={() => onStart(s)}
          title={s.isActive ? 'Already running — stop it first' : undefined}
        >
          {s.isActive ? (
            'Running'
          ) : (
            <>
              <IconPlay style={{ width: 13, height: 13 }} /> Start…
            </>
          )}
        </button>
      </div>
    </article>
  )
}

/* ------------------------------------------------------- parameter editing */

interface ParamRow {
  key: string
  value: string
}

function parseParamDefaults(json: string): ParamRow[] {
  try {
    const obj = JSON.parse(json || '{}') as Record<string, unknown>
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return []
    return Object.entries(obj).map(([key, value]) => ({
      key,
      value: typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value),
    }))
  } catch {
    return []
  }
}

/** "70" -> 70, "true" -> true, anything else stays a string (as on DeployPage). */
function coerceParam(value: string): unknown {
  const trimmed = value.trim()
  if (trimmed === 'true') return true
  if (trimmed === 'false') return false
  if (trimmed !== '' && !Number.isNaN(Number(trimmed))) return Number(trimmed)
  return trimmed
}

function ParamGrid({ rows, onChange }: { rows: ParamRow[]; onChange: (rows: ParamRow[]) => void }) {
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

/* ------------------------------------------------------------ launch dialog */

function UnderlyingPicker({
  list,
  supported,
  value,
  onChange,
}: {
  list: FnoUnderlying[]
  supported: Set<string>
  value: string | null
  onChange: (underlying: string) => void
}) {
  return (
    <div className="pick-list" role="radiogroup" aria-label="Underlying">
      {list.map((u) => {
        const ok = supported.has(u.underlying.toUpperCase())
        const active = value === u.underlying
        return (
          <button
            key={u.underlying}
            type="button"
            role="radio"
            aria-checked={active}
            className={`pick-row ${active ? 'is-active' : ''}`}
            disabled={!ok}
            onClick={() => onChange(u.underlying)}
            title={ok ? u.spotSymbol : 'Not supported by this strategy'}
          >
            <span className="pick-row__name">
              {u.underlying} <span className="faint" style={{ fontWeight: 500 }}>{u.exchange}</span>
            </span>
            <span className="pick-row__tag">
              {ok ? `${formatNumber(u.optionContracts)} contracts` : 'not supported by this strategy'}
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
  const cardRef = useRef<HTMLDivElement>(null)

  const supported = useMemo(
    () => new Set(strategy.supportedUnderlyings.map((u) => u.toUpperCase())),
    [strategy.supportedUnderlyings],
  )

  const [underlying, setUnderlying] = useState<string | null>(null)
  const [lots, setLots] = useState(String(Math.max(1, strategy.defaultLots || 1)))
  const [stopLoss, setStopLoss] = useState('')
  const [target, setTarget] = useState('')
  const [capital, setCapital] = useState(String(1_000_000))
  const [advanced, setAdvanced] = useState(false)
  const [params, setParams] = useState<ParamRow[]>(() =>
    parseParamDefaults(strategy.defaultParametersJson),
  )
  const [validation, setValidation] = useState<string | null>(null)

  const list = underlyings.data ?? []
  const firstSupported = list.find((u) => supported.has(u.underlying.toUpperCase())) ?? null
  const chosen = list.find((u) => u.underlying === underlying) ?? null

  // Default to the first supported underlying once the list is in.
  useEffect(() => {
    if (underlying == null && firstSupported) setUnderlying(firstSupported.underlying)
  }, [underlying, firstSupported])

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

  const lotsNum = Number(lots)
  const units = chosen && Number.isInteger(lotsNum) && lotsNum > 0 ? lotsNum * chosen.lotSize : null

  function submit() {
    setValidation(null)
    if (!chosen) {
      setValidation('Pick an underlying — the strategy must know what it trades.')
      return
    }
    if (!Number.isInteger(lotsNum) || lotsNum < 1) {
      setValidation('Lots must be a whole number of at least 1.')
      return
    }
    const sl = stopLoss.trim() === '' ? null : Number(stopLoss)
    if (sl != null && !(sl > 0)) {
      setValidation('Stop-loss must be a positive rupee amount, or left empty.')
      return
    }
    const tg = target.trim() === '' ? null : Number(target)
    if (tg != null && !(tg > 0)) {
      setValidation('Target must be a positive rupee amount, or left empty.')
      return
    }
    const cap = Number(capital)
    if (!(cap > 0)) {
      setValidation('Capital must be a positive amount.')
      return
    }
    const entries = params
      .filter((p) => p.key.trim() !== '')
      .map((p) => [p.key.trim(), coerceParam(p.value)] as const)
    const body: StartStrategyRequest = {
      underlying: chosen.underlying,
      lots: lotsNum,
      stopLoss: sl,
      target: tg,
      parameters: entries.length > 0 ? Object.fromEntries(entries) : null,
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

  const canStart = !!chosen && !start.isPending

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
        <aside className="modal__aside">
          <div>
            <h2 className="modal__title" id="launch-title">
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
                  value={underlying}
                  onChange={setUnderlying}
                />
                {!firstSupported && (
                  <span className="field__help warn">
                    None of the loaded underlyings is supported by this strategy.
                  </span>
                )}
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
                  value={underlying}
                  onChange={setUnderlying}
                />
                {!firstSupported && (
                  <span className="field__help warn">
                    None of the loaded underlyings is supported by this strategy.
                  </span>
                )}
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
            <div className="field">
              <label className="field__label" htmlFor="launch-sl">
                Stop-loss ₹
              </label>
              <input
                id="launch-sl"
                className="field__input"
                type="number"
                min={0}
                step={100}
                inputMode="decimal"
                placeholder="none"
                value={stopLoss}
                onChange={(e) => setStopLoss(e.target.value)}
              />
              <span className="field__help">leave empty for none · applies to total P&L of this run</span>
            </div>
            <div className="field">
              <label className="field__label" htmlFor="launch-target">
                Target ₹
              </label>
              <input
                id="launch-target"
                className="field__input"
                type="number"
                min={0}
                step={100}
                inputMode="decimal"
                placeholder="none"
                value={target}
                onChange={(e) => setTarget(e.target.value)}
              />
              <span className="field__help">leave empty for none · applies to total P&L of this run</span>
            </div>
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
