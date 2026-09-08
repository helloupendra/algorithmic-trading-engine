/**
 * Trading module — the filter lab.
 *
 * A bench for the indicators and the market-context gate: point them at a real
 * instrument, see what they compute, and see whether a signal would have been
 * allowed — and if not, which rule stopped it.
 *
 * It calls the SAME Python the live runner and the backtest call. Nothing here
 * recomputes anything in the browser, because a second implementation would be
 * the one that drifts and this page would then be proving the wrong thing.
 *
 * Read-only. It books no orders and changes no run.
 */

import { useState } from 'react'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { useEvaluateFilters } from '../../lib/queries'
import type { LabResult } from '../../lib/queries'

const PRESETS: { label: string; filters: Record<string, unknown> }[] = [
  { label: 'None', filters: {} },
  {
    label: 'Trend',
    filters: { ema_period: 20, require_ema_side: true, require_vwap_side: true },
  },
  {
    label: 'Trend + range',
    filters: {
      ema_period: 20,
      require_ema_side: true,
      require_vwap_side: true,
      min_atr_percent: 0.03,
    },
  },
  {
    label: 'Session hours',
    filters: { trade_window_ist: ['09:30', '15:00'], block_open_minutes: 15 },
  },
  {
    label: 'Candle confirmation',
    filters: { require_pattern: 'auto' },
  },
]

function num(value: number | null | undefined, dp = 2): string {
  return value == null ? '—' : value.toFixed(dp)
}

function IndicatorGrid({ result }: { result: LabResult }) {
  const i = result.indicators
  const cells: { label: string; value: string; hint?: string }[] = [
    { label: 'Close', value: num(i.close), hint: i.barIst ?? undefined },
    { label: 'VWAP', value: num(i.vwap), hint: `${i.sessionBars} session bars` },
    { label: `EMA ${i.emaPeriod}`, value: num(i.ema) },
    { label: 'SMA 20', value: num(i.sma20) },
    { label: 'RSI 14', value: num(i.rsi14, 1) },
    { label: 'ATR 14', value: num(i.atr14) },
    { label: 'ATR %', value: num(i.atrPercent, 3) },
    {
      label: 'Volume z-score',
      value: i.volumeZScore == null ? 'n/a' : num(i.volumeZScore),
      hint: i.volumeZScore == null ? 'no volume on this feed' : undefined,
    },
  ]

  return (
    <div className="stat-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))' }}>
      {cells.map((c) => (
        <div className="stat" key={c.label}>
          <div className="stat__value mono">{c.value}</div>
          <div className="stat__label">{c.label}</div>
          {c.hint && <div className="stat__sub">{c.hint}</div>}
        </div>
      ))}
    </div>
  )
}

export function FilterLabPage() {
  const [symbol, setSymbol] = useState('NSE:NIFTYBANK-INDEX')
  const [resolution, setResolution] = useState('5m')
  const today = new Date().toISOString().slice(0, 10)
  const [fromDate, setFromDate] = useState(today)
  const [toDate, setToDate] = useState(today)
  const [fromTime, setFromTime] = useState('09:15')
  const [toTime, setToTime] = useState('15:30')
  const [filtersText, setFiltersText] = useState(JSON.stringify(PRESETS[1].filters, null, 2))
  const [jsonError, setJsonError] = useState<string | null>(null)

  const run = useEvaluateFilters()
  const result = run.data

  function applyPreset(preset: (typeof PRESETS)[number]) {
    setFiltersText(JSON.stringify(preset.filters, null, 2))
    setJsonError(null)
  }

  function evaluate() {
    let filters: unknown = {}
    if (filtersText.trim() !== '') {
      try {
        filters = JSON.parse(filtersText)
      } catch (e) {
        setJsonError(e instanceof Error ? e.message : 'Not valid JSON.')
        return
      }
    }
    setJsonError(null)
    run.mutate({
      symbol: symbol.trim(),
      resolution,
      fromDate,
      toDate: toDate || fromDate,
      fromTime,
      toTime,
      filters,
    })
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Filter lab</h1>
          <p className="page__subtitle">
            Run the indicators and the market-context filters over real bars and see exactly what they
            compute. This calls the same Python the live runner and the backtest use — nothing is
            recalculated here — so what you see is what a run would act on. It places no orders.
          </p>
        </div>
      </header>

      <Panel title="Setup">
        <div className="form-row">
          <div className="field">
            <label className="field__label" htmlFor="lab-symbol">Instrument</label>
            <SymbolCombobox id="lab-symbol" value={symbol} onChange={setSymbol} />
            <p className="field__help">Index, future, option — anything with bars.</p>
          </div>

          <div className="field">
            <label className="field__label" htmlFor="lab-res">Timeframe</label>
            <select
              id="lab-res"
              className="field__input"
              value={resolution}
              onChange={(e) => setResolution(e.target.value)}
            >
              <option value="1m">1m</option>
              <option value="5m">5m</option>
              <option value="15m">15m</option>
            </select>
          </div>

          <div className="field">
            <label className="field__label" htmlFor="lab-from-date">From date</label>
            <input id="lab-from-date" className="field__input" type="date"
                   value={fromDate} onChange={(e) => setFromDate(e.target.value)} />
            <p className="field__help">Stored candles go back about two months.</p>
          </div>

          <div className="field">
            <label className="field__label" htmlFor="lab-to-date">To date</label>
            <input id="lab-to-date" className="field__input" type="date"
                   value={toDate} onChange={(e) => setToDate(e.target.value)} />
          </div>

          <div className="field">
            <label className="field__label" htmlFor="lab-from-time">From time (IST)</label>
            <input id="lab-from-time" className="field__input" type="time"
                   value={fromTime} onChange={(e) => setFromTime(e.target.value)} />
          </div>

          <div className="field">
            <label className="field__label" htmlFor="lab-to-time">To time (IST)</label>
            <input id="lab-to-time" className="field__input" type="time"
                   value={toTime} onChange={(e) => setToTime(e.target.value)} />
            <p className="field__help">Every closed candle in the window is judged.</p>
          </div>
        </div>

        <div className="field" style={{ marginTop: 12 }}>
          <label className="field__label" htmlFor="lab-filters">Filters</label>
          <div className="row" style={{ gap: 6, flexWrap: 'wrap', marginBottom: 8 }}>
            {PRESETS.map((p) => (
              <button
                key={p.label}
                type="button"
                className="btn btn--ghost btn--sm"
                onClick={() => applyPreset(p)}
              >
                {p.label}
              </button>
            ))}
          </div>
          <textarea
            id="lab-filters"
            className="field__input mono"
            style={{ minHeight: 150, resize: 'vertical' }}
            value={filtersText}
            onChange={(e) => setFiltersText(e.target.value)}
            spellCheck={false}
          />
          <p className={`field__help ${jsonError ? 'neg' : ''}`}>
            {jsonError ?? 'The same object a run carries in parametersJson.filters — paste it straight across.'}
          </p>
        </div>

        <button type="button" className="btn btn--primary" onClick={evaluate} disabled={run.isPending}>
          {run.isPending ? 'Evaluating…' : 'Evaluate'}
        </button>

        {run.isError && <InlineError error={run.error} />}
      </Panel>

      {run.isPending && <Loading label="Running the indicators over real bars…" />}

      {result?.error && (
        <Panel title="Result">
          <p className="neg">{result.error}</p>
        </Panel>
      )}

      {result && !result.error && (
        <>
          <Panel
            title="Indicators"
            actions={
              <span className="faint" style={{ fontSize: 12 }}>
                {result.closedBarCount} {result.resolution} bars from {result.source} · last {result.indicators.barIst} IST
              </span>
            }
          >
            <IndicatorGrid result={result} />
            <div className="deploy-card__meta muted" style={{ marginTop: 10, flexWrap: 'wrap' }}>
              <span>Patterns on the last candle:</span>
              {result.indicators.patterns.length === 0 ? (
                <span className="faint">none detected</span>
              ) : (
                result.indicators.patterns.map((p) => (
                  <Badge key={p} tone="accent">
                    {p.replace(/_/g, ' ')}
                  </Badge>
                ))
              )}
            </div>
          </Panel>

          <Panel title="Which side is open right now">
            {!result.filtersConfigured ? (
              <EmptyState>
                No filters configured — both sides would pass. Add a rule above to see one bite.
              </EmptyState>
            ) : (
              <div className="form-row">
                {(['bullish', 'bearish'] as const).map((side) => {
                  const v = result.verdicts[side]
                  return (
                    <div key={side} className="stat" style={{ alignItems: 'flex-start' }}>
                      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                        <Badge tone={v.allowed ? 'pos' : 'neg'}>
                          {side === 'bullish' ? 'CE / long' : 'PE / short'} —{' '}
                          {v.allowed ? 'ALLOWED' : `blocked by ${v.blockedBy}`}
                        </Badge>
                      </div>
                      <div className="stat__sub" style={{ marginTop: 6 }}>{v.reason}</div>
                    </div>
                  )
                })}
              </div>
            )}
            <p className="field__help" style={{ marginTop: 10 }}>
              A market the filters refuse a call in is usually one they allow a put in. Both sides are
              judged on the same candle, so "blocked" tells you which side was open — not that there was
              no trade.
            </p>
          </Panel>

          <Panel
            title="Over the last candles"
            actions={
              <span className="faint" style={{ fontSize: 12 }}>
                CE {result.summary.bullish.allowed} · PE {result.summary.bearish.allowed} allowed of{' '}
                {result.summary.evaluated}
              </span>
            }
          >
            <div className="deploy-card__meta muted" style={{ marginBottom: 10, flexWrap: 'wrap' }}>
              {(['bullish', 'bearish'] as const).map((side) => {
                const t = result.summary[side]
                const entries = Object.entries(t.blockedBy).sort((a, b) => b[1] - a[1])
                return (
                  <span key={side} style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <strong>{side === 'bullish' ? 'CE' : 'PE'}:</strong>
                    {entries.length === 0 ? (
                      <Badge tone="pos">never blocked</Badge>
                    ) : (
                      entries.map(([rule, count]) => (
                        <Badge key={rule} tone="warn">
                          {rule} × {count}
                        </Badge>
                      ))
                    )}
                  </span>
                )
              })}
            </div>

            {/* Capped and scrolled: 200 candles is a legitimate request and the
                page must not become a mile long because of it. */}
            <div className="tablewrap" style={{ maxHeight: 460, overflowY: 'auto' }}>
              <table className="table">
                <thead>
                  <tr>
                    <th>Candle (IST)</th>
                    <th className="r">Close</th>
                    <th>CE / long</th>
                    <th>PE / short</th>
                    <th>Why the blocked side was blocked</th>
                  </tr>
                </thead>
                <tbody>
                  {[...result.timeline].reverse().map((row, i) => (
                    <tr key={i}>
                      <td className="mono">{row.barIst}</td>
                      <td className="r mono">{row.close}</td>
                      <td>
                        {row.bullish.allowed ? (
                          <Badge tone="pos">allowed</Badge>
                        ) : (
                          <Badge tone="neg">{row.bullish.blockedBy}</Badge>
                        )}
                      </td>
                      <td>
                        {row.bearish.allowed ? (
                          <Badge tone="pos">allowed</Badge>
                        ) : (
                          <Badge tone="neg">{row.bearish.blockedBy}</Badge>
                        )}
                      </td>
                      <td className="muted">
                        {row.bullish.allowed ? row.bearish.reason : row.bullish.reason}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Panel>
        </>
      )}
    </div>
  )
}
