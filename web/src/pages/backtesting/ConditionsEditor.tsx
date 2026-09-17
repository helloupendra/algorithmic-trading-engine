/**
 * The setup editor for a strategy written in the console (SignalBuilder): the
 * conditions that make a long setup, and the ones that make a short one.
 *
 * One condition per line, in the engine's own words, so what is on screen is
 * exactly what the run stores and what a report prints back. The list of what
 * can be written is beside the boxes rather than in a manual.
 */

import { useState } from 'react'
import { IconChevronDown, IconChevronRight } from '../../components/icons'

/** What the engine understands (strategies/builder/conditions.py). */
export const CONDITION_HELP: Array<[string, string]> = [
  ['close>vwap', 'closed above the session VWAP (needs traded volume)'],
  ['close>ema:9', 'above the 9 EMA — any period, and < for below'],
  ['ema:9>ema:21', 'the fast EMA above the slow one'],
  ['rsi>60', 'RSI(14) level; rsi<40 for the mirror'],
  ['rsi_cross_up:60', 'RSI crossed 60 on this very bar (the spike)'],
  ['rsi_cross_down:40', 'the same downwards'],
  ['body>=0.5', 'the candle’s body is at least half its range'],
  ['green', 'closed up; red for the other way'],
  ['supertrend:bullish', 'Supertrend(10,3) direction'],
  ['adx>20', 'trend strength, whichever way it points'],
  ['break_high:15', 'closed above the first 15 minutes’ high; break_low:15 below its low'],
  ['above_open', 'above the session’s own open; below_open for the mirror'],
  ['gap>0.5', 'the session gapped more than 0.5%'],
  ['volume_z>1.5', 'volume z-score (an index has none)'],
]

export function ConditionsEditor({
  long,
  short,
  onChange,
  disabled,
}: {
  long: string
  short: string
  onChange: (next: { long: string; short: string }) => void
  disabled?: boolean
}) {
  const [help, setHelp] = useState(false)

  return (
    <div className="conditions">
      <div className="conditions__pair">
        <div className="field">
          <label className="field__label" htmlFor="cond-long">
            Long setup — every line must hold
          </label>
          <textarea
            id="cond-long"
            className="field__input conditions__box"
            rows={4}
            spellCheck={false}
            value={long}
            disabled={disabled}
            placeholder={'close>vwap\nclose>ema:9\nbody>=0.5\nrsi_cross_up:60'}
            onChange={(e) => onChange({ long: e.target.value, short })}
          />
          <span className="field__help">Leave it empty and the strategy never goes long.</span>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="cond-short">
            Short setup
          </label>
          <textarea
            id="cond-short"
            className="field__input conditions__box"
            rows={4}
            spellCheck={false}
            value={short}
            disabled={disabled}
            placeholder={'close<vwap\nclose<ema:9\nbody>=0.5\nrsi_cross_down:40'}
            onChange={(e) => onChange({ long, short: e.target.value })}
          />
          <span className="field__help">A bar that argues both ways is left alone.</span>
        </div>
      </div>

      <button type="button" className="disclosure__btn" aria-expanded={help} onClick={() => setHelp((v) => !v)}>
        {help ? <IconChevronDown /> : <IconChevronRight />}
        What can be written here
      </button>
      {help && (
        <ul className="conditions__help">
          {CONDITION_HELP.map(([text, meaning]) => (
            <li key={text}>
              <code>{text}</code>
              <span>{meaning}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** The parameter value (a list, or one line per condition) as editor text. */
export function conditionsToText(value: unknown): string {
  if (Array.isArray(value)) return value.map((x) => String(x)).join('\n')
  if (typeof value === 'string') return value.split(',').map((x) => x.trim()).filter(Boolean).join('\n')
  return ''
}

/** Editor text back to the list the engine stores. */
export function textToConditions(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
}
