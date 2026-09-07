import { describe, expect, it } from 'vitest'

import { CELLS,
  ROTATION,
  STRIKES,
  bandFor,
  baseProfile,
  createChainModel,
  mulberry32, ageOf, slotAtAge } from './chainModel'

/**
 * The hero's object is only worth looking at if it is the poller's ring buffer
 * and not a decorative surface. These pin the three behaviours the object has
 * to show — money-first polling, carry-forward with null-never-zero, discrete
 * rounds — plus the shape facts (walls, round-number clustering) and the
 * determinism every screenshot relies on.
 */

const SEED = 20260907

function value(m: ReturnType<typeof createChainModel>, slot: number, strike: number, side: 0 | 1) {
  return m.values[slot * CELLS + strike * 2 + side]
}

describe('createChainModel', () => {
  it('is deterministic from its seed', () => {
    const a = createChainModel(SEED, 24)
    const b = createChainModel(SEED, 24)
    expect(Array.from(a.values)).toEqual(Array.from(b.values))
    expect(Array.from(a.spot)).toEqual(Array.from(b.spot))
    for (let r = 0; r < 24; r++) {
      a.advance()
      b.advance()
    }
    expect(Array.from(a.values)).toEqual(Array.from(b.values))
    expect(a.head).toBe(b.head)
  })

  it('clusters open interest on round-number strikes', () => {
    const base = baseProfile(mulberry32(SEED))
    for (let i = 5; i < STRIKES - 1; i += 5) {
      for (let side = 0; side < 2; side++) {
        const here = base[i * 2 + side]
        const around = (base[(i - 1) * 2 + side] + base[(i + 1) * 2 + side]) / 2
        expect(here / around).toBeGreaterThanOrEqual(1.5)
      }
    }
  })

  it('puts the call wall above spot and the put wall below', () => {
    const base = baseProfile(mulberry32(SEED))
    let callNum = 0
    let callDen = 0
    let putNum = 0
    let putDen = 0
    for (let i = 0; i < STRIKES; i++) {
      callNum += i * base[i * 2]
      callDen += base[i * 2]
      putNum += i * base[i * 2 + 1]
      putDen += base[i * 2 + 1]
    }
    expect(callNum / callDen).toBeGreaterThan(20)
    expect(putNum / putDen).toBeLessThan(20)
  })

  it('polls one band per round, nearest to ATM first, and covers every strike in one pass', () => {
    expect(bandFor(0)).toEqual([0, 4])
    expect(bandFor(1)).toEqual([3, 8])
    expect(bandFor(4)).toEqual([15, 20])
    expect(bandFor(5)).toEqual([19, STRIKES])
    expect(bandFor(6)).toEqual([0, 4])

    // Every rolling window of six rounds polls every strike, even though ATM
    // steps between rounds — that is what the one-strike overlap buys.
    const m = createChainModel(SEED, 12)
    const polledAt: number[][] = []
    for (let r = 0; r < 600; r++) {
      m.advance()
      const [lo, hi] = m.band()
      const atm = m.atm()
      const polled: number[] = []
      for (let i = 0; i < STRIKES; i++) {
        const dist = Math.abs(i - atm)
        if (dist >= lo && dist < hi) polled.push(i)
      }
      polledAt.push(polled)
    }
    for (let start = 0; start + ROTATION <= polledAt.length; start++) {
      const seen = new Set<number>()
      for (let r = start; r < start + ROTATION; r++) polledAt[r].forEach((i) => seen.add(i))
      expect(seen.size).toBe(STRIKES)
    }
  })

  it('carries unpolled strikes forward exactly and never writes zero', () => {
    const m = createChainModel(SEED, 12)
    for (let r = 0; r < 40; r++) {
      const prev = m.head
      m.advance()
      const [lo, hi] = m.band()
      const atm = m.atm()
      for (let i = 0; i < STRIKES; i++) {
        const dist = Math.abs(i - atm)
        const polled = dist >= lo && dist < hi
        for (const side of [0, 1] as const) {
          const now = value(m, m.head, i, side)
          if (polled) {
            expect(now).toBeGreaterThanOrEqual(0.02)
            expect(now).toBeLessThanOrEqual(1)
          } else {
            expect(now).toBe(value(m, prev, i, side))
          }
        }
      }
    }
  })

  it('starts fresh: round 0 has null wings, and no nulls remain after one full pass', () => {
    const m = createChainModel(SEED, 24)
    const atm0 = Math.round(m.spot[0])
    for (let i = 0; i < STRIKES; i++) {
      const isNull = value(m, 0, i, 0) === 0 && value(m, 0, i, 1) === 0
      expect(isNull).toBe(Math.abs(i - atm0) >= 4)
    }
    for (let slot = ROTATION - 1; slot < 24; slot++) {
      for (let k = 0; k < CELLS; k++) {
        const v = m.values[slot * CELLS + k]
        expect(v).toBeGreaterThanOrEqual(0.02)
        expect(v).toBeLessThanOrEqual(1)
      }
    }
  })

  it('keeps spot inside the chain over ten thousand rounds', () => {
    const m = createChainModel(SEED, 8)
    for (let r = 0; r < 10_000; r++) {
      m.advance()
      const s = m.spot[m.head]
      expect(s).toBeGreaterThanOrEqual(14)
      expect(s).toBeLessThanOrEqual(26)
    }
  })

  it('ring-buffers: the head advances one slot per round and wraps', () => {
    const m = createChainModel(SEED, 8)
    expect(m.head).toBe(7)
    expect(m.round).toBe(8)
    m.advance()
    expect(m.head).toBe(0)
    expect(m.round).toBe(9)
  })
})


/**
 * The ring convention is read by three renderers — the GL shader, the wire and
 * the SVG twin — and they must agree to the slot. The one time the shader had
 * the operands reversed, the oldest round drew directly behind the newest, its
 * never-polled sockets showed as dark dashes across the wings, and every round
 * boundary jolted the whole object two slots forward.
 */
describe('ring age convention', () => {
  it('age 0 is the head, age 1 the previous round, n-1 the oldest', () => {
    expect(slotAtAge(5, 0, 24)).toBe(5)
    expect(slotAtAge(5, 1, 24)).toBe(4)
    expect(slotAtAge(5, 23, 24)).toBe(6)
  })

  it('wraps below zero', () => {
    expect(slotAtAge(0, 1, 24)).toBe(23)
    expect(slotAtAge(2, 5, 24)).toBe(21)
  })

  it('ageOf inverts slotAtAge for every slot', () => {
    const n = 24
    for (let head = 0; head < n; head++) {
      for (let age = 0; age < n; age++) {
        expect(ageOf(slotAtAge(head, age, n), head, n)).toBe(age)
      }
    }
  })

  it('the GL shader uses the same arithmetic as the CPU', async () => {
    // Pinned as a string on purpose: the GLSL cannot be executed here, and the
    // two operand orders are the whole difference between right and reversed.
    const { BAR_VERT } = await import('../components/ChainScene')
    expect(BAR_VERT).toContain('mod(uHead - aCell.z + uN, uN)')
    expect(BAR_VERT).not.toContain('mod(aCell.z - uHead')
  })
})
