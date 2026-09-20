import { describe, expect, it } from 'vitest'
import { RUN, SESSIONS } from './evidence'

/**
 * The homepage draws the losing run from SESSIONS and prints RUN. These tests
 * make sure the two agree with each other and with the console's own report,
 * so the picture can never quietly say something the ledger does not.
 */
describe('the run the homepage shows in full', () => {
  it('has one row per trading session, in order, with no repeats', () => {
    expect(SESSIONS).toHaveLength(RUN.sessions)
    const days = SESSIONS.map((s) => s[0])
    expect([...days].sort()).toEqual(days)
    expect(new Set(days).size).toBe(days.length)
    expect(days[0]).toBe(RUN.from)
    expect(days[days.length - 1]).toBe(RUN.to)
  })

  it('starts on the account it says it started on', () => {
    expect(SESSIONS[0][1]).toBe(RUN.initialCapital)
  })

  it('adds up to the net P&L, the trade count and the profitable days the console reports', () => {
    const pnl = SESSIONS.reduce((a, s) => a + s[5], 0)
    // Per-day rounding: each session's P&L is rounded to the rupee before it is summed.
    expect(Math.abs(pnl - RUN.netPnl)).toBeLessThanOrEqual(10)
    expect(SESSIONS.reduce((a, s) => a + s[6], 0)).toBe(RUN.trades)
    expect(SESSIONS.filter((s) => s[5] > 0)).toHaveLength(RUN.profitableSessions)
    expect(Math.round(RUN.netPnl / RUN.trades)).toBe(RUN.perTrade)
  })

  it('touches the lowest balance and ends where the console says', () => {
    expect(Math.min(...SESSIONS.map((s) => s[3]))).toBe(RUN.lowestEquity)
    expect(Math.abs(SESSIONS[SESSIONS.length - 1][4] - RUN.closingEquity)).toBeLessThanOrEqual(1)
  })

  it('falls from its peak by the drawdown the console reports', () => {
    let peak = 0
    let dd = 0
    for (const s of SESSIONS) {
      peak = Math.max(peak, s[2])
      dd = Math.max(dd, peak - s[3])
    }
    expect(dd).toBe(RUN.maxDrawdown)
  })

  it('keeps every session\'s high above its low, open and close', () => {
    for (const s of SESSIONS) {
      expect(s[2]).toBeGreaterThanOrEqual(Math.max(s[1], s[4]))
      expect(s[3]).toBeLessThanOrEqual(Math.min(s[1], s[4]))
    }
  })

  it('ends below where it started — this is the losing run, not a pitch', () => {
    expect(RUN.netPnl).toBeLessThan(0)
    expect(SESSIONS[SESSIONS.length - 1][4]).toBeLessThan(RUN.initialCapital)
  })
})
