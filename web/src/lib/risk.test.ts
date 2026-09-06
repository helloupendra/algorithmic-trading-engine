import { describe, expect, it } from 'vitest'

import {
  EMPTY_RISK_DRAFT,
  normalizeRisk,
  parseRiskDraft,
  riskDraftFrom,
} from './risk'
import type { RiskRules } from './types'

/**
 * The risk draft is the last thing between a number someone typed and the
 * engine that will trade on it.
 *
 * Both bugs pinned here shipped together in one change and reached the screen:
 * a validation loop that walked every draft key ran the non-numeric `scope`
 * through the number parser and rendered "undefined must be undefined", and
 * the normaliser silently dropped `scope`, so the selector changed nothing.
 * Neither was visible to the type checker, and neither would have been caught
 * by reading the diff.
 */
describe('parseRiskDraft', () => {
  it('keeps a rupee target', () => {
    const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, overallTarget: '3000' })
    expect(result.error).toBeNull()
    expect(result.rules?.overall?.target).toBe(3000)
  })

  it('does not run the scope through the number parser', () => {
    // `Object.keys(draft)` is typed string[], so casting it to the numeric
    // field union asserted something untrue and the compiler stayed quiet.
    const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, overallTarget: '3000' })
    expect(result.error).toBeNull()
  })

  it('names the field when a number cannot be read', () => {
    const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, overallTarget: 'abc' })
    expect(result.field).toBe('overallTarget')
    expect(result.error).toContain('Overall target')
    expect(result.error).not.toContain('undefined')
  })

  it('never renders undefined in an error, whichever field is bad', () => {
    for (const key of Object.keys(EMPTY_RISK_DRAFT)) {
      if (key === 'overallScope') continue
      const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, [key]: 'abc' })
      expect(result.error, key).not.toContain('undefined')
      expect(result.field, key).toBe(key)
    }
  })

  it('defaults an overall rule to per-day', () => {
    const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, overallTarget: '3000' })
    expect(result.rules?.overall?.scope).toBe('day')
  })

  it('sends the whole-run choice through to the rules', () => {
    // Dropped here, the selector would look like it worked and change nothing.
    const result = parseRiskDraft({
      ...EMPTY_RISK_DRAFT,
      overallTarget: '3000',
      overallScope: 'run',
    })
    expect(result.rules?.overall?.scope).toBe('run')
  })

  it('still requires a trail distance beside a trigger', () => {
    const result = parseRiskDraft({ ...EMPTY_RISK_DRAFT, overallTrailTrigger: '500' })
    expect(result.field).toBe('overallTrailTrigger')
    expect(result.error).toContain('only says when the trail arms')
  })
})

describe('normalizeRisk', () => {
  it('emits nothing when nothing is set', () => {
    expect(normalizeRisk({})).toEqual({})
  })

  it('does not invent an overall rule just to carry a scope', () => {
    // A bare {scope:'day'} would make the block non-empty, and every run would
    // claim an overall rule it does not have.
    const out = normalizeRisk({ overall: { scope: 'day' }, leg: { stopLossPoints: 20 } } as RiskRules)
    expect(out.overall).toBeUndefined()
    expect(out.leg?.stopLossPoints).toBe(20)
  })

  it('carries the scope alongside a real rule', () => {
    const out = normalizeRisk({ overall: { target: 3000, scope: 'run' } } as RiskRules)
    expect(out.overall).toEqual({ target: 3000, scope: 'run' })
  })

  it('drops non-positive amounts, which every reader treats as unset', () => {
    const out = normalizeRisk({ overall: { target: 0, stopLoss: -1 } } as RiskRules)
    expect(out.overall).toBeUndefined()
  })
})

describe('riskDraftFrom', () => {
  it('round-trips the scope so Run again reopens what was run', () => {
    const draft = riskDraftFrom({ overall: { target: 3000, scope: 'run' } } as RiskRules)
    expect(draft.overallScope).toBe('run')
    expect(draft.overallTarget).toBe('3000')
  })

  it('reads a stored rule with no scope as per-day', () => {
    const draft = riskDraftFrom({ overall: { target: 3000 } } as RiskRules)
    expect(draft.overallScope).toBe('day')
  })

  it('survives no rules at all', () => {
    expect(riskDraftFrom(null)).toEqual(EMPTY_RISK_DRAFT)
  })

  it('comes back out of parseRiskDraft unchanged', () => {
    const rules: RiskRules = { overall: { target: 3000, scope: 'run' }, leg: { stopLossPoints: 20 } }
    const again = parseRiskDraft(riskDraftFrom(rules))
    expect(again.error).toBeNull()
    expect(again.rules).toEqual(normalizeRisk(rules))
  })
})
