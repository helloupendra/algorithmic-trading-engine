import { describe, expect, it } from 'vitest'
import { familyName, hasOwnName, strategyName, strategyNameWithId } from './strategyNames'

describe('familyName', () => {
  it('names the TrendRider variants the trend family', () => {
    expect(familyName('TrendRider')).toBe('Trend family')
    expect(familyName('Ghost')).toBe('Ghost variants')
  })
})

describe('strategyName', () => {
  it('gives the trend family the owner’s names', () => {
    expect(strategyName('TrendRider')).toBe('Opener')
    expect(strategyName('TrendRiderV2')).toBe('Breaker')
    expect(strategyName('TrendRiderV2BE')).toBe('Guard')
    expect(strategyName('TrendRiderV2OB')).toBe('Sniper')
  })

  it('leaves every other strategy as its id', () => {
    expect(strategyName('GhostTangentCrossingsV2')).toBe('GhostTangentCrossingsV2')
    expect(strategyName('CrudeMomentum')).toBe('CrudeMomentum')
  })

  it('matches the id exactly, not by case or prefix', () => {
    expect(strategyName('trendrider')).toBe('trendrider')
    expect(strategyName('TrendRiderV2OBX')).toBe('TrendRiderV2OBX')
  })

  it('handles a missing id', () => {
    expect(strategyName(undefined)).toBe('')
    expect(strategyName(null)).toBe('')
    expect(strategyName('')).toBe('')
  })

  it('keeps the id beside a renamed strategy', () => {
    expect(hasOwnName('TrendRiderV2OB')).toBe(true)
    expect(hasOwnName('Ghost')).toBe(false)
    expect(strategyNameWithId('TrendRiderV2OB')).toBe('Sniper (TrendRiderV2OB)')
    expect(strategyNameWithId('Ghost')).toBe('Ghost')
  })
})
