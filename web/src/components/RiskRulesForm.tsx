/**
 * Risk rules editor — the compact three-row grid used by the launch dialog,
 * the backtest dialog and the run card's inline editor:
 *
 *   Overall   ₹ stop-loss · ₹ target           → a hit ends the run
 *   Per group ₹ stop-loss · ₹ target           → closes that group only
 *   Per leg   SL pts · target pts · SL % · target %  → closes that leg only
 *
 * Every field is optional ("not set"). The component is controlled over a
 * string-valued RiskDraft so a parent keeps the same submit-time validation
 * it uses for lots and capital: `parseRiskDraft(draft)` returns the RiskRules
 * to send or the first "> 0" violation (and which field), which the parent
 * hands back as `invalidField` to highlight it. A field that already holds a
 * non-positive number is marked invalid as the user types.
 */

import { useEffect, useRef } from 'react'
import { riskFieldInvalid } from '../lib/risk'
import type { RiskDraft, RiskDraftField } from '../lib/risk'

interface FieldSpec {
  key: RiskDraftField
  label: string
  step: number
  inputMode: 'decimal' | 'numeric'
  title: string
}

interface LevelSpec {
  name: string
  help: string
  fields: FieldSpec[]
}

const LEVELS: LevelSpec[] = [
  {
    name: 'Overall',
    help: 'on total P&L of the run (realized + unrealized) · a hit squares off everything and ends the run',
    fields: [
      { key: 'overallStopLoss', label: 'Stop-loss ₹', step: 100, inputMode: 'decimal', title: 'Rupees of total loss that end the run' },
      { key: 'overallTarget', label: 'Target ₹', step: 100, inputMode: 'decimal', title: 'Rupees of total profit that end the run' },
    ],
  },
  {
    name: 'Per group',
    help: 'on each group’s P&L (one entry, e.g. a straddle pair) · closes that group only; the run keeps going',
    fields: [
      { key: 'groupStopLoss', label: 'Stop-loss ₹', step: 100, inputMode: 'decimal', title: 'Rupees of loss on one group that close it' },
      { key: 'groupTarget', label: 'Target ₹', step: 100, inputMode: 'decimal', title: 'Rupees of profit on one group that close it' },
    ],
  },
  {
    name: 'Per leg',
    help: 'premium points vs entry and/or % of entry premium · closes that leg only; when both are set, the first to trip wins',
    fields: [
      { key: 'legStopLossPoints', label: 'SL points', step: 1, inputMode: 'decimal', title: 'Adverse premium move in points that closes the leg' },
      { key: 'legTargetPoints', label: 'Target points', step: 1, inputMode: 'decimal', title: 'Favourable premium move in points that closes the leg' },
      { key: 'legStopLossPercent', label: 'SL %', step: 0.5, inputMode: 'decimal', title: 'Adverse move as % of entry premium that closes the leg' },
      { key: 'legTargetPercent', label: 'Target %', step: 0.5, inputMode: 'decimal', title: 'Favourable move as % of entry premium that closes the leg' },
    ],
  },
]

export function RiskRulesForm({
  value,
  onChange,
  idPrefix,
  disabled = false,
  invalidField = null,
  invalidNonce = 0,
  autoFocus = false,
}: {
  value: RiskDraft
  onChange: (next: RiskDraft) => void
  /** Makes input ids unique when several forms are on one page (one per run card). */
  idPrefix: string
  disabled?: boolean
  /** The field a submit-time parse rejected — highlighted and focused. */
  invalidField?: RiskDraftField | null
  /**
   * Bumped by the parent on every failed submit, so the field is re-focused
   * when the SAME field fails again (React batches `setInvalidField(null)`
   * followed by `setInvalidField(field)` into no change, which would leave
   * the effect below silent on the second click).
   */
  invalidNonce?: number
  /** Focus the first input on mount (the run card's inline editor). */
  autoFocus?: boolean
}) {
  const firstRef = useRef<HTMLInputElement>(null)
  const invalidRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (autoFocus) firstRef.current?.focus()
  }, [autoFocus])

  useEffect(() => {
    if (invalidField) {
      const el = invalidRef.current
      if (el) {
        el.focus()
        el.scrollIntoView?.({ block: 'nearest' })
      }
    }
  }, [invalidField, invalidNonce])

  function set(key: RiskDraftField, text: string) {
    onChange({ ...value, [key]: text })
  }

  let first = true
  return (
    <div className="risk-form" role="group" aria-label="Risk rules">
      {LEVELS.map((level) => (
        <div key={level.name} className="risk-form__row">
          <div className="risk-form__level">
            <span className="risk-form__name">{level.name}</span>
            <span className="risk-form__help">{level.help}</span>
          </div>
          <div className="risk-form__fields">
            {level.fields.map((f) => {
              const id = `${idPrefix}-${f.key}`
              const invalid = invalidField === f.key || riskFieldInvalid(value[f.key])
              const isFirst = first
              first = false
              return (
                <div key={f.key} className="risk-form__field">
                  <label htmlFor={id}>{f.label}</label>
                  <input
                    id={id}
                    ref={invalidField === f.key ? invalidRef : isFirst ? firstRef : undefined}
                    className="field__input"
                    type="number"
                    min={0}
                    step={f.step}
                    inputMode={f.inputMode}
                    placeholder="not set"
                    title={f.title}
                    value={value[f.key]}
                    disabled={disabled}
                    aria-invalid={invalid || undefined}
                    onChange={(e) => set(f.key, e.target.value)}
                  />
                </div>
              )
            })}
          </div>
        </div>
      ))}
    </div>
  )
}
