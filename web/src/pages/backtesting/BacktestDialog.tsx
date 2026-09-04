/**
 * Backtesting module — the launch dialog. Same shell as the live LaunchDialog
 * (strategy aside, underlying pick-list, lots / stop-loss / target, Advanced
 * parameters) plus what only a replay needs: a resolution choice built from
 * what is actually stored for that underlying, a date range bounded by that
 * coverage, the end-of-day square-off time, charges and data caveats.
 *
 * Coverage first: nothing here can be picked before the dialog has shown how
 * many sessions exist for it, and Start is blocked over a range with none.
 */

import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useBacktestBackfill, useBacktestCoverage, useFnoUnderlyings, useStartBacktest } from '../../lib/queries'
import { formatInrWhole, formatNumber } from '../../lib/format'
import { resolutionLabel, resolutionRank, toCandleResolution } from '../../lib/symbols'
import { parseRiskDraft, riskDraftFrom, riskFromLegacy } from '../../lib/risk'
import type { RiskDraft, RiskDraftField } from '../../lib/risk'
import { InlineError, Loading } from '../../components/ui'
import { RiskRulesForm } from '../../components/RiskRulesForm'
import { IconChevronDown, IconChevronRight, IconPlay, IconX } from '../../components/icons'
import type {
  BacktestCoverageResolution,
  RiskRules,
  StartBacktestRequest,
  StartBacktestResponse,
  StrategyListItem,
} from '../../lib/types'
import {
  ParamGrid,
  StrategyAside,
  StrikeSelection,
  UnderlyingPicker,
  mergeParams,
  parseParamDefaults,
  useDialogChrome,
  useStrikeSelection,
  type ParamRow,
} from '../strategies/shared'
import { parseStrikeParams } from '../../lib/contracts'
import {
  addDays,
  countWeekdays,
  formatDayRange,
  istDate,
  RESERVED_PARAM_KEYS,
  todayIst,
} from './shared'

const DEFAULT_EOD = '15:15'
const DEFAULT_CAPITAL = 1_000_000

/** Pre-fill for "Run again": the values of an earlier run. */
export interface BacktestDialogInitial {
  underlying?: string
  resolution?: string
  fromDate?: string
  toDate?: string
  lots?: number
  /** Legacy overall stop-loss — used only when `risk` is absent. */
  stopLoss?: number | null
  /** Legacy overall target — used only when `risk` is absent. */
  target?: number | null
  risk?: RiskRules | null
  eodSquareOffIst?: string | null
  chargesPerLot?: number
  initialCapital?: number
  parametersJson?: string
}

/** The rules an earlier run was under: the three-level object, else its overall shorthands. */
function initialRiskDraft(initial: BacktestDialogInitial | undefined): RiskDraft {
  if (!initial) return riskDraftFrom(null)
  return riskDraftFrom(initial.risk ?? riskFromLegacy(initial.stopLoss, initial.target))
}

function clampDate(value: string, min: string | null, max: string | null): string {
  let v = value
  if (min && v < min) v = min
  if (max && v > max) v = max
  return v
}

/**
 * Only backfilled candles can drive a replay. Live 1m bars (source "live")
 * are reported by the coverage endpoint so the reader knows they exist, but
 * the runner reads the candles table only and the API rejects such a range.
 */
function replayable(r: BacktestCoverageResolution | null | undefined): boolean {
  return !!r && r.source === 'backfill' && r.barCount > 0
}

export function BacktestDialog({
  strategy,
  onClose,
  onStarted,
  initial,
}: {
  strategy: StrategyListItem
  onClose: () => void
  onStarted?: (response: StartBacktestResponse) => void
  initial?: BacktestDialogInitial
}) {
  const underlyings = useFnoUnderlyings()
  const start = useStartBacktest()
  const backfill = useBacktestBackfill()
  const cardRef = useDialogChrome(onClose)

  const supported = useMemo(
    () => new Set(strategy.supportedUnderlyings.map((u) => u.toUpperCase())),
    [strategy.supportedUnderlyings],
  )
  const strategyRequired = useMemo(
    () => new Set(strategy.dataRequirements.map((d) => toCandleResolution(d.resolution))),
    [strategy.dataRequirements],
  )

  const [underlying, setUnderlying] = useState<string | null>(initial?.underlying ?? null)
  const [resolution, setResolution] = useState<string | null>(
    initial?.resolution ? toCandleResolution(initial.resolution) : null,
  )
  const [fromDate, setFromDate] = useState(initial?.fromDate ?? '')
  const [toDate, setToDate] = useState(initial?.toDate ?? '')
  const [seededFor, setSeededFor] = useState<string | null>(null)
  const [lots, setLots] = useState(String(Math.max(1, initial?.lots ?? strategy.defaultLots ?? 1)))
  const [risk, setRisk] = useState<RiskDraft>(() => initialRiskDraft(initial))
  const [riskField, setRiskField] = useState<RiskDraftField | null>(null)
  // Bumped on every failed submit so the form re-focuses the same field on a repeat.
  const [riskNonce, setRiskNonce] = useState(0)
  const [eodNone, setEodNone] = useState(initial?.eodSquareOffIst === '')
  const [eodTime, setEodTime] = useState(initial?.eodSquareOffIst || DEFAULT_EOD)
  const [charges, setCharges] = useState(String(initial?.chargesPerLot ?? 0))
  const [capital, setCapital] = useState(String(initial?.initialCapital ?? DEFAULT_CAPITAL))
  const [advanced, setAdvanced] = useState(false)
  // Strike distances get their own inputs above; the free-form grid drops them
  // along with the keys the API merges into every run's parametersJson.
  const strike = useStrikeSelection(strategy, initial?.parametersJson)
  const [strikeField, setStrikeField] = useState<string | null>(null)
  const [params, setParams] = useState<ParamRow[]>(() =>
    parseParamDefaults(
      initial?.parametersJson ?? strategy.defaultParametersJson,
      new Set([...RESERVED_PARAM_KEYS, ...strike.omitKeys]),
    ),
  )
  const [backfilling, setBackfilling] = useState<string | null>(null)
  const [validation, setValidation] = useState<string | null>(null)

  const list = underlyings.data ?? []
  const firstSupported = list.find((u) => supported.has(u.underlying.toUpperCase())) ?? null
  const chosenUnderlying = list.find((u) => u.underlying === underlying) ?? null

  useEffect(() => {
    if (underlying == null && firstSupported) setUnderlying(firstSupported.underlying)
  }, [underlying, firstSupported])

  const coverage = useBacktestCoverage(underlying, strategy.id)
  // The query keeps the previous underlying's answer as a placeholder while
  // the new one loads; only an answer for the chosen underlying counts.
  const cov =
    coverage.data && underlying && coverage.data.underlying.toUpperCase() === underlying.toUpperCase()
      ? coverage.data
      : undefined
  const coverageLoading = !!underlying && (coverage.isPending || (coverage.isFetching && !cov))
  const rows = useMemo(
    () =>
      [...(cov?.resolutions ?? [])].sort(
        (a, b) => resolutionRank(a.resolution) - resolutionRank(b.resolution),
      ),
    [cov],
  )
  const chosen: BacktestCoverageResolution | null =
    rows.find((r) => r.resolution === resolution) ?? null

  // Default resolution: the strategy's required one if it is replayable, else
  // the first replayable resolution. Re-evaluated when the underlying changes.
  useEffect(() => {
    if (!cov) return
    if (resolution && rows.some((r) => r.resolution === resolution && replayable(r))) return
    const withData = rows.filter(replayable)
    const required = withData.find((r) => strategyRequired.has(r.resolution))
    const pick = required ?? withData[0] ?? null
    if (pick && pick.resolution !== resolution) setResolution(pick.resolution)
  }, [cov, rows, resolution, strategyRequired])

  // Index resolutions the strategy declares that are not replayable: the API
  // refuses such a run (the runner would feed the strategy an empty series).
  const missingRequired = useMemo(
    () => rows.filter((r) => strategyRequired.has(r.resolution) && r.resolution !== resolution && !replayable(r)),
    [rows, strategyRequired, resolution],
  )

  const today = todayIst()
  const minDate = chosen ? istDate(chosen.firstUtc) : null
  const maxDateRaw = chosen ? istDate(chosen.lastUtc) : null
  const maxDate = maxDateRaw && maxDateRaw > today ? today : maxDateRaw

  // Seed the date range with the full covered span once per (underlying,
  // resolution); a pre-filled range is clamped into the coverage instead.
  useEffect(() => {
    if (!chosen || !minDate || !maxDate) return
    const key = `${underlying}|${chosen.resolution}`
    if (seededFor === key) return
    setSeededFor(key)
    const wantFrom = seededFor == null && initial?.fromDate ? initial.fromDate : minDate
    const wantTo = seededFor == null && initial?.toDate ? initial.toDate : maxDate
    setFromDate(clampDate(wantFrom, minDate, maxDate))
    setToDate(clampDate(wantTo, minDate, maxDate))
  }, [chosen, minDate, maxDate, underlying, seededFor, initial])

  function pickUnderlying(u: string) {
    setUnderlying(u)
    setResolution(null)
    setSeededFor(null)
    setValidation(null)
  }

  // Sessions inside the chosen range — exact when the range is the whole
  // coverage, otherwise a weekday count capped by what is stored.
  const sessionsInRange = useMemo(() => {
    if (!chosen || !minDate || !maxDate || !fromDate || !toDate) return null
    const lo = fromDate > minDate ? fromDate : minDate
    const hi = toDate < maxDate ? toDate : maxDate
    if (hi < lo || toDate < fromDate) return { count: 0, exact: true }
    if (lo === minDate && hi === maxDate) return { count: chosen.sessions, exact: true }
    return { count: Math.min(chosen.sessions, countWeekdays(lo, hi)), exact: false }
  }, [chosen, minDate, maxDate, fromDate, toDate])

  const lotsNum = Number(lots)
  const lotSize = cov?.lotSize ?? chosenUnderlying?.lotSize ?? null
  const units = lotSize != null && Number.isInteger(lotsNum) && lotsNum > 0 ? lotsNum * lotSize : null

  function runBackfill(res: string) {
    if (!underlying) return
    setBackfilling(res)
    backfill.mutate(
      {
        underlying,
        resolutions: [res],
        fromDate: fromDate || addDays(today, -30),
        toDate: toDate || today,
      },
      { onSettled: () => setBackfilling(null) },
    )
  }

  function submit() {
    setValidation(null)
    setRiskField(null)
    setStrikeField(null)
    if (!chosenUnderlying) {
      setValidation('Pick an underlying — the strategy must know what it trades.')
      return
    }
    if (!chosen || !replayable(chosen)) {
      setValidation('Pick a resolution that has backfilled index candles (backfill one first if none has).')
      return
    }
    if (missingRequired.length > 0) {
      setValidation(
        `The strategy needs ${missingRequired.map((r) => r.label).join(' and ')} index candles and none are backfilled for ${chosenUnderlying.underlying} — backfill them first.`,
      )
      return
    }
    if (!fromDate || !toDate || toDate < fromDate) {
      setValidation('The date range must run from an earlier day to a later one.')
      return
    }
    if (toDate > today) {
      setValidation('The range cannot end after today.')
      return
    }
    if (!sessionsInRange || sessionsInRange.count === 0) {
      setValidation(
        `No ${chosenUnderlying.underlying} ${resolutionLabel(chosen.resolution)} sessions between ${formatDayRange(fromDate, toDate)} — pick dates inside ${formatDayRange(minDate, maxDate)} or backfill first.`,
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
    const parsedRisk = parseRiskDraft(risk)
    if (parsedRisk.rules === null) {
      setRiskField(parsedRisk.field)
      setRiskNonce((n) => n + 1)
      setValidation(parsedRisk.error)
      return
    }
    if (!eodNone && !/^([01]\d|2[0-3]):[0-5]\d$/.test(eodTime)) {
      setValidation('End-of-day square-off must be an HH:MM time (IST), or ticked as none.')
      return
    }
    const chg = Number(charges)
    if (!(chg >= 0)) {
      setValidation('Charges per lot must be zero or a positive rupee amount.')
      return
    }
    const cap = Number(capital)
    if (!(cap > 0)) {
      setValidation('Capital must be a positive amount.')
      return
    }
    const body: StartBacktestRequest = {
      strategyId: strategy.id,
      underlying: chosenUnderlying.underlying,
      resolution: chosen.resolution,
      fromDate,
      toDate,
      lots: lotsNum,
      // The legacy fields mirror the overall level so an API build from
      // before the three-level rules still applies them.
      stopLoss: parsedRisk.rules.overall?.stopLoss ?? null,
      target: parsedRisk.rules.overall?.target ?? null,
      risk: parsedRisk.rules,
      eodSquareOffIst: eodNone ? '' : eodTime,
      chargesPerLot: chg,
      parameters: mergeParams(params, strikes.values),
      initialCapital: cap,
    }
    start.mutate(body, {
      onSuccess: (response) => {
        onStarted?.(response)
        onClose()
      },
    })
  }

  const notes: ReactNode[] = [
    <li key="premiums">
      Option premiums are fetched from FYERS history per contract on demand; expired contracts have
      no history — trades on them will be listed as skipped.
    </li>,
  ]
  if (cov && !cov.brokerLinked)
    notes.push(
      <li key="broker">
        Broker not linked: only contracts already stored can be priced. Restore the{' '}
        <Link to="/admin/broker">broker session</Link> to fetch missing premiums.
      </li>,
    )
  if (cov)
    notes.push(
      <li key="lot">
        Lot size {cov.lotSize}
        {cov.lotSizeSource !== 'master' ? ` (${cov.lotSizeSource})` : ''} is today's — historical
        lot-size changes are not modelled.
      </li>,
    )
  if (cov && cov.optionCandles.symbols > 0)
    notes.push(
      <li key="opt">
        {formatNumber(cov.optionCandles.symbols)} {cov.underlying} option contracts already stored (
        {formatDayRange(cov.optionCandles.firstUtc, cov.optionCandles.lastUtc)}).
      </li>,
    )
  for (const r of missingRequired) {
    notes.push(
      <li key={`req-${r.resolution}`} className="warn">
        The strategy declares {r.label} bars and none are backfilled for {cov?.underlying} — the run
        cannot start until they are (use the Backfill button on that resolution).
      </li>,
    )
  }
  for (const [i, n] of (cov?.notes ?? []).entries()) notes.push(<li key={`n${i}`}>{n}</li>)

  const canStart =
    !!chosenUnderlying &&
    replayable(chosen) &&
    missingRequired.length === 0 &&
    !!sessionsInRange &&
    sessionsInRange.count > 0 &&
    !start.isPending

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
        aria-labelledby="backtest-title"
        tabIndex={-1}
        ref={cardRef}
      >
        <StrategyAside strategy={strategy} titleId="backtest-title" />

        <div className="modal__body">
          <div className="modal__head">
            <span className="section-title" style={{ margin: 0 }}>
              Backtest over stored history
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
            ) : list.length === 0 ? (
              <div className="alert alert--warn" role="status">
                <span>
                  No F&O contracts loaded — import the instrument master first on{' '}
                  <Link to="/admin/data/instruments">Data › Instruments & F&O</Link>.
                </span>
              </div>
            ) : (
              <>
                {underlyings.isError && (
                  <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
                    Refresh failed — showing the last loaded data.
                  </p>
                )}
                <UnderlyingPicker
                  list={list}
                  supported={supported}
                  value={underlying}
                  onChange={pickUnderlying}
                />
                {!firstSupported && (
                  <span className="field__help warn">
                    None of the loaded underlyings is supported by this strategy.
                  </span>
                )}
              </>
            )}
          </div>

          <div className="field">
            <span className="field__label">Resolution (index candles stored for {underlying ?? '…'})</span>
            {!underlying ? (
              <span className="field__help">Pick an underlying first.</span>
            ) : coverageLoading ? (
              <Loading label="Checking stored history…" />
            ) : coverage.isError && !cov ? (
              <InlineError error={coverage.error} />
            ) : (
              <>
                <div className="seg seg--wrap" role="radiogroup" aria-label="Resolution">
                  {rows.map((r) => {
                    const hasData = replayable(r)
                    const liveOnly = !hasData && r.source === 'live' && r.barCount > 0
                    const active = r.resolution === resolution
                    const button = (
                      <button
                        key={r.resolution}
                        type="button"
                        role="radio"
                        aria-checked={active}
                        className={`seg__btn seg__btn--meta ${active ? 'is-active' : ''}`}
                        disabled={!hasData}
                        onClick={() => {
                          setResolution(r.resolution)
                          setValidation(null)
                        }}
                        title={
                          hasData
                            ? `${formatNumber(r.barCount)} bars · backfilled candles`
                            : liveOnly
                              ? `${formatNumber(r.barCount)} live 1m bars from ingestion sessions — not replayable; backfill 1m candles to use this resolution`
                              : r.backfillable
                                ? 'Nothing stored — backfill it from FYERS'
                                : 'Nothing stored and no broker session to fetch it'
                        }
                      >
                        <span>
                          {r.label}
                          {strategyRequired.has(r.resolution) ? ' · strategy' : ''}
                        </span>
                        <small>
                          {hasData
                            ? `${formatNumber(r.sessions)} ${r.sessions === 1 ? 'session' : 'sessions'} · ${formatDayRange(r.firstUtc, r.lastUtc)}`
                            : liveOnly
                              ? 'live bars only — not replayable'
                              : 'no data'}
                        </small>
                      </button>
                    )
                    if (hasData || !r.backfillable) return button
                    return (
                      <span key={r.resolution} className="seg__group">
                        {button}
                        <button
                          type="button"
                          className="btn btn--ghost btn--sm"
                          disabled={!cov?.brokerLinked || backfill.isPending}
                          onClick={() => runBackfill(r.resolution)}
                          title={
                            cov?.brokerLinked
                              ? `Fetch ${r.label} candles for ${fromDate || addDays(today, -30)} → ${toDate || today}`
                              : 'FYERS is not linked'
                          }
                        >
                          {backfilling === r.resolution ? 'Fetching…' : 'Backfill'}
                        </button>
                      </span>
                    )
                  })}
                </div>
                {!rows.some(replayable) && (
                  <span className="field__help warn">
                    No backfilled index candles for {cov?.spotSymbol ?? underlying} at any resolution — backfill
                    one to make this underlying backtestable.
                  </span>
                )}
                {backfill.isSuccess && backfill.data && (
                  <span className="field__help pos" role="status">
                    {backfill.data.message}
                    {backfill.data.perResolution.length > 0 &&
                      ` — ${backfill.data.perResolution
                        .map((p) => `${resolutionLabel(p.resolution)}: ${formatNumber(p.candlesFetched)} candles`)
                        .join(', ')}`}
                  </span>
                )}
                {backfill.isError && <InlineError error={backfill.error} />}
              </>
            )}
          </div>

          <div className="form-row">
            <div className="field">
              <label className="field__label" htmlFor="bt-from">
                From (IST day)
              </label>
              <input
                id="bt-from"
                className="field__input"
                type="date"
                min={minDate ?? undefined}
                max={maxDate ?? today}
                value={fromDate}
                onChange={(e) => setFromDate(e.target.value)}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="bt-to">
                To (IST day)
              </label>
              <input
                id="bt-to"
                className="field__input"
                type="date"
                min={minDate ?? undefined}
                max={maxDate ?? today}
                value={toDate}
                onChange={(e) => setToDate(e.target.value)}
              />
            </div>
            <div className="field">
              <span className="field__label">Coverage</span>
              <span className="field__help" style={{ paddingBottom: 9 }}>
                {chosen && minDate && maxDate ? (
                  <>
                    {sessionsInRange
                      ? `${sessionsInRange.exact ? '' : '≈ '}${formatNumber(sessionsInRange.count)} ${sessionsInRange.count === 1 ? 'session' : 'sessions'} in range`
                      : 'pick both dates'}
                    {' · '}
                    stored {formatDayRange(minDate, maxDate)}
                    {(fromDate !== minDate || toDate !== maxDate) && (
                      <>
                        {' · '}
                        <button
                          type="button"
                          className="btn btn--ghost btn--sm"
                          style={{ padding: '0 4px' }}
                          onClick={() => {
                            setFromDate(minDate)
                            setToDate(maxDate)
                          }}
                        >
                          Full range
                        </button>
                      </>
                    )}
                  </>
                ) : (
                  'bounded by the chosen resolution once it has data'
                )}
              </span>
            </div>
          </div>

          <div className="form-row">
            <div className="field">
              <label className="field__label" htmlFor="bt-lots">
                Lots
              </label>
              <input
                id="bt-lots"
                className="field__input"
                type="number"
                min={1}
                step={1}
                inputMode="numeric"
                value={lots}
                onChange={(e) => setLots(e.target.value)}
              />
              <span className="field__help">
                {units != null ? `= ${formatNumber(units)} units (lot size ${lotSize})` : 'whole lots of the chosen underlying'}
              </span>
            </div>
            <div className="field">
              <label className="field__label" htmlFor="bt-eod">
                EOD square-off (IST)
              </label>
              <input
                id="bt-eod"
                className="field__input"
                type="time"
                value={eodTime}
                disabled={eodNone}
                onChange={(e) => setEodTime(e.target.value)}
              />
              <label className="field__help" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                <input type="checkbox" checked={eodNone} onChange={(e) => setEodNone(e.target.checked)} />
                none — carry positions overnight
              </label>
            </div>
          </div>

          {strike.requirements.length > 0 && (
            <div className="field">
              <span className="field__label">
                Strike selection
                {chosenUnderlying
                  ? ` (on the ${chosenUnderlying.underlying} grid, step ${chosenUnderlying.strikeStep})`
                  : ''}
              </span>
              <StrikeSelection
                requirements={strike.requirements}
                underlying={chosenUnderlying}
                values={strike.values}
                onChange={(next) => {
                  strike.setValues(next)
                  setStrikeField(null)
                }}
                idPrefix="bt-strike"
                invalidParam={strikeField}
              />
              <span className="field__help">
                A contract the stored history cannot price is a skipped entry, listed in the run's data
                notes.
              </span>
            </div>
          )}

          <div className="field">
            <span className="field__label">Risk rules (all optional · evaluated every bar, leg → group → overall)</span>
            <RiskRulesForm
              value={risk}
              onChange={(next) => {
                setRisk(next)
                setRiskField(null)
              }}
              idPrefix="bt-risk"
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
                · charges, capital and parameters
              </span>
            </button>
            {advanced && (
              <div className="disclosure__body" style={{ display: 'grid', gap: 12 }}>
                <div className="form-row">
                  <div className="field" style={{ maxWidth: 260 }}>
                    <label className="field__label" htmlFor="bt-charges">
                      Charges per lot ₹
                    </label>
                    <input
                      id="bt-charges"
                      className="field__input"
                      type="number"
                      min={0}
                      step={5}
                      value={charges}
                      onChange={(e) => setCharges(e.target.value)}
                    />
                    <span className="field__help">flat, per lot, per fill · 0 = no brokerage/slippage</span>
                  </div>
                  <div className="field" style={{ maxWidth: 260 }}>
                    <label className="field__label" htmlFor="bt-capital">
                      Capital ({formatInrWhole(Number(capital) || 0)})
                    </label>
                    <input
                      id="bt-capital"
                      className="field__input"
                      type="number"
                      min={100000}
                      step={100000}
                      value={capital}
                      onChange={(e) => setCapital(e.target.value)}
                    />
                  </div>
                </div>
                <div className="field">
                  <span className="field__label">Parameters</span>
                  <ParamGrid rows={params} onChange={setParams} />
                </div>
              </div>
            )}
          </div>

          <div className="field">
            <span className="field__label">Data notes</span>
            <ul className="data-notes">{notes}</ul>
          </div>

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
                : `Start backtest on ${chosenUnderlying?.underlying ?? '…'}${chosen ? ` · ${resolutionLabel(chosen.resolution)}` : ''}`}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
