import { describe, expect, it } from 'vitest'
import { barPath, niceStep, niceTicks } from './bars'

describe('the value axis', () => {
  it('ends on clean ticks around the values, zero among them', () => {
    // FII and DII cash on Fri 25 Sep 2026 and the day before, ₹ crore.
    expect(niceTicks(-5027.36, 2838.17).ticks).toEqual([-6000, -4000, -2000, 0, 2000, 4000])
    // FIIs net short all month: the axis stops at zero.
    expect(niceTicks(-312001, 0)).toEqual({ min: -400000, max: 0, ticks: [-400000, -300000, -200000, -100000, 0] })
  })

  it('steps in fractions for a share that hardly moves', () => {
    expect(niceTicks(10.8, 12.5).ticks).toEqual([10.5, 11, 11.5, 12, 12.5])
  })

  it('still has a step when every value is the same', () => {
    const one = niceTicks(0, 0)
    expect(one.max).toBeGreaterThan(one.min)
    expect(one.ticks).toContain(0)
  })

  it('keeps the daily P&L step unchanged', () => {
    expect(niceStep(18_000)).toBe(5000)
    expect(niceStep(3)).toBe(1)
  })
})

describe('a bar', () => {
  it('is square at its baseline and rounded at its data end, either side of it', () => {
    expect(barPath(10, 8, 100, 40, 3)).toBe('M10,100 V43 Q10,40 13,40 H15 Q18,40 18,43 V100 Z')
    expect(barPath(10, 8, 100, 160, 3)).toBe('M10,100 V157 Q10,160 13,160 H15 Q18,160 18,157 V100 Z')
  })

  it('rounds a short bar no further than half its length', () => {
    expect(barPath(0, 8, 100, 98, 4)).toBe('M0,100 V99 Q0,98 1,98 H7 Q8,98 8,99 V100 Z')
  })
})
