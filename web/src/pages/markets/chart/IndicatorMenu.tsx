/**
 * The chart's indicators: a button at the end of the toolbar that opens one
 * short list of switches, the overlays on the price first (SMA and EMA at the
 * reader's period, session VWAP, Bollinger Bands, Supertrend) and the panes
 * under it after (RSI, MACD). The maths is lib/indicators.ts; the choice is
 * remembered in this browser (lib/chart.ts, readIndicatorSettings).
 */

import { useCallback, useRef, useState } from 'react'
import { useDismiss } from '../../../components/shell/useDismiss'
import { PERIOD_MAX, PERIOD_MIN, parsePeriod } from '../../../lib/chart'
import type { IndicatorKey, IndicatorSettings } from '../../../lib/chart'

/** What each switch draws, in the order the list shows them; `numbers` are the fixed parameters. */
const OVERLAYS: ReadonlyArray<{ key: IndicatorKey; label: string; numbers?: string; title: string }> = [
  { key: 'sma', label: 'SMA', title: 'Simple moving average of the closes' },
  { key: 'ema', label: 'EMA', title: 'Exponential moving average of the closes, seeded with the SMA of its first period' },
  {
    key: 'vwap',
    label: 'VWAP',
    numbers: 'session',
    title: 'Volume-weighted average of the typical price, (high + low + close) / 3, from the session open; it starts again every session',
  },
  { key: 'bollinger', label: 'Bollinger Bands', numbers: '20, 2', title: 'The 20-candle SMA and two standard deviations either side of it' },
  { key: 'supertrend', label: 'Supertrend', numbers: '10, 3', title: "Three ATRs (Wilder's, 10 candles) from the median price; a close through the line turns it" },
]

const PANES: ReadonlyArray<{ key: IndicatorKey; label: string; numbers: string; title: string }> = [
  { key: 'rsi', label: 'RSI', numbers: '14', title: "Wilder's relative strength index of the closes, with 70 and 30 marked" },
  { key: 'macd', label: 'MACD', numbers: '12, 26, 9', title: 'The 12-candle EMA less the 26, its 9-candle EMA as the signal, and the histogram between them' },
]

/** A period box that only hands on a whole number in range; anything else waits until the box is left. */
function PeriodInput({ label, value, onChange }: { label: string; value: number; onChange: (period: number) => void }) {
  const [draft, setDraft] = useState(String(value))
  return (
    <input
      type="number"
      inputMode="numeric"
      className="field__input ind__period"
      aria-label={`${label} period`}
      min={PERIOD_MIN}
      max={PERIOD_MAX}
      step={1}
      value={draft}
      onChange={(e) => {
        setDraft(e.target.value)
        const period = parsePeriod(e.target.value)
        if (period != null && period !== value) onChange(period)
      }}
      onBlur={() => setDraft(String(value))}
    />
  )
}

export function IndicatorMenu({
  value,
  onChange,
  intraday,
  hasVolume,
  disabled,
}: {
  value: IndicatorSettings
  onChange: (next: IndicatorSettings) => void
  /** VWAP is a session's: a day or a week chart has none. */
  intraday: boolean
  /** Whether the candles on screen traded any volume; an index reports none, so its VWAP is empty. */
  hasVolume: boolean
  /** While the SMC layer is on: its chart draws its own marks, not these. */
  disabled: boolean
}) {
  const [open, setOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)
  const close = useCallback(() => setOpen(false), [])
  useDismiss(boxRef, open, close)

  // What is drawn here, so a week chart does not count a VWAP it cannot have.
  const count = (Object.keys(value.on) as IndicatorKey[]).filter((k) => value.on[k] && (k !== 'vwap' || intraday)).length
  const flip = (key: IndicatorKey) => onChange({ ...value, on: { ...value.on, [key]: !value.on[key] } })
  // A period typed is a line wanted: it turns its average on.
  const setPeriod = (key: 'sma' | 'ema', period: number) =>
    onChange({ ...value, [key === 'sma' ? 'smaPeriod' : 'emaPeriod']: period, on: { ...value.on, [key]: true } })

  const vwapNote = !intraday
    ? 'Intraday charts only'
    : !hasVolume && value.on.vwap
      ? 'No traded volume here (an index has none), so there is no VWAP to draw'
      : null

  function row(item: { key: IndicatorKey; label: string; numbers?: string; title: string }) {
    const off = item.key === 'vwap' && !intraday
    return (
      <div key={item.key} className="ind__row">
        <label className="ind__switch" title={item.title}>
          <input type="checkbox" checked={value.on[item.key] && !off} disabled={off} onChange={() => flip(item.key)} />
          <span className="ind__name">{item.label}</span>
          {item.numbers && <span className="ind__numbers">{item.numbers}</span>}
        </label>
        {item.key === 'sma' && <PeriodInput label="SMA" value={value.smaPeriod} onChange={(p) => setPeriod('sma', p)} />}
        {item.key === 'ema' && <PeriodInput label="EMA" value={value.emaPeriod} onChange={(p) => setPeriod('ema', p)} />}
        {item.key === 'vwap' && vwapNote && <span className="ind__note">{vwapNote}</span>}
      </div>
    )
  }

  return (
    <div className="ind" ref={boxRef}>
      <div className="seg">
        <button
          type="button"
          className={`seg__btn ind__btn${count > 0 && !disabled ? ' is-active' : ''}`}
          aria-haspopup="dialog"
          aria-expanded={open}
          disabled={disabled}
          title={disabled ? 'Indicators draw on the price layer; turn SMC off to see them' : 'Indicators over the price and in panes under it'}
          onClick={() => setOpen((v) => !v)}
        >
          Indicators
          {count > 0 && <span className="ind__count">{count}</span>}
        </button>
      </div>
      {open && !disabled && (
        <div className="ind__pop" role="dialog" aria-label="Indicators">
          <div className="ind__section">On the price</div>
          {OVERLAYS.map(row)}
          <div className="ind__section">Under the chart</div>
          {PANES.map(row)}
          <p className="ind__foot">
            Remembered in this browser. SMA, EMA, RSI, Supertrend and VWAP are computed as the strategies compute them.
          </p>
        </div>
      )}
    </div>
  )
}
