import { describe, expect, it } from 'vitest'
import { ATM, STRIKES, SESSION_MINUTES, ladder, rng, tape } from './synth'

describe('rng', () => {
  it('is deterministic and stays in [0, 1)', () => {
    const a = rng(7)
    const b = rng(7)
    for (let i = 0; i < 1000; i++) {
      const x = a()
      expect(x).toBe(b())
      expect(x).toBeGreaterThanOrEqual(0)
      expect(x).toBeLessThan(1)
    }
  })
  it('does not get stuck on a zero seed', () => {
    const r = rng(0)
    expect(new Set([r(), r(), r()]).size).toBe(3)
  })
})

describe('tape', () => {
  const t = tape(20260920)
  it('has one candle per session minute', () => {
    expect(t).toHaveLength(SESSION_MINUTES)
  })
  it('stays inside the unit range including its wicks', () => {
    for (const c of t) {
      expect(c.base).toBeGreaterThanOrEqual(0)
      expect(c.base + c.body).toBeLessThanOrEqual(1)
      expect(c.body).toBeGreaterThanOrEqual(0.025)
      expect(c.wickUp).toBeGreaterThan(0)
      expect(c.wickDown).toBeGreaterThan(0)
    }
  })
  it('is the same tape on every load', () => {
    expect(tape(20260920)).toEqual(t)
    expect(tape(1)).not.toEqual(t)
  })
  it('has both colours', () => {
    const ups = t.filter((c) => c.up).length
    expect(ups).toBeGreaterThan(100)
    expect(ups).toBeLessThan(275)
  })
})

describe('ladder', () => {
  it('gives a length in (0, 1] for every call and put', () => {
    const l = ladder(3, 105)
    expect(l).toHaveLength(STRIKES * 2)
    for (const v of l) {
      expect(v).toBeGreaterThan(0)
      expect(v).toBeLessThanOrEqual(1)
    }
  })
  it('peaks away from the money, on opposite sides for calls and puts', () => {
    const l = ladder(3, 105)
    const calls = Array.from(l.slice(0, STRIKES))
    const puts = Array.from(l.slice(STRIKES))
    expect(calls.indexOf(Math.max(...calls))).toBeLessThan(ATM)
    expect(puts.indexOf(Math.max(...puts))).toBeGreaterThan(ATM)
  })
  it('changes with the minute, so the chain visibly rebuilds', () => {
    const a = ladder(3, 105)
    const b = ladder(3, 20)
    let diff = 0
    for (let i = 0; i < a.length; i++) diff += Math.abs(a[i] - b[i])
    expect(diff).toBeGreaterThan(0.5)
  })
})
