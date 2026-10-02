import { describe, expect, it } from 'vitest'
import { estimateSessions, sessionsOnHand } from './readings'

describe('estimateSessions', () => {
  it('divides a bar count by a full session at that resolution', () => {
    expect(estimateSessions('1', 375 * 10)).toBe(10)
    expect(estimateSessions('5', 75 * 10)).toBe(10)
    expect(estimateSessions('15', 25 * 10)).toBe(10)
    expect(estimateSessions('D', 10)).toBe(10)
  })
  it('reads the live recorder\'s "1m" as one minute, in either case', () => {
    expect(estimateSessions('1m', 750)).toBe(2)
    expect(estimateSessions('1M', 750)).toBe(2)
    expect(estimateSessions('d', 3)).toBe(3)
  })
  it('counts a part of a session as one, and no bars as none', () => {
    expect(estimateSessions('1', 1)).toBe(1)
    expect(estimateSessions('1', 376)).toBe(2)
    expect(estimateSessions('5', 0)).toBe(0)
  })
  it('treats an unknown resolution as five minutes', () => {
    expect(estimateSessions('30', 150)).toBe(2)
  })
})

/** Symbol, resolution, bars: the index rows of the coverage endpoint as recorded on 27 Sep 2026. */
const RECORDED: [string, string, number][] = [
  ['BSE:BANKEX-INDEX', '1', 2_969],
  ['BSE:BANKEX-INDEX', '15', 205],
  ['BSE:BANKEX-INDEX', '1m', 3_246],
  ['BSE:BANKEX-INDEX', '5', 604],
  ['BSE:SENSEX-INDEX', '1', 476_385],
  ['BSE:SENSEX-INDEX', '15', 31_809],
  ['BSE:SENSEX-INDEX', '1m', 5_797],
  ['BSE:SENSEX-INDEX', '5', 95_322],
  ['BSE:SENSEX-INDEX', 'D', 1_274],
  ['NSE:FINNIFTY-INDEX', '1', 30_649],
  ['NSE:FINNIFTY-INDEX', '15', 2_049],
  ['NSE:FINNIFTY-INDEX', '1m', 6_373],
  ['NSE:FINNIFTY-INDEX', '5', 6_134],
  ['NSE:FINNIFTY-INDEX', 'D', 6],
  ['NSE:INDIAVIX-INDEX', '1', 475_510],
  ['NSE:INDIAVIX-INDEX', '15', 31_798],
  ['NSE:INDIAVIX-INDEX', '1m', 3_933],
  ['NSE:INDIAVIX-INDEX', '5', 95_291],
  ['NSE:INDIAVIX-INDEX', 'D', 1_274],
  ['NSE:MIDCPNIFTY-INDEX', '1', 3_617],
  ['NSE:MIDCPNIFTY-INDEX', '15', 246],
  ['NSE:MIDCPNIFTY-INDEX', '1m', 3_909],
  ['NSE:MIDCPNIFTY-INDEX', '5', 732],
  ['NSE:NIFTY50-INDEX', '1', 572_763],
  ['NSE:NIFTY50-INDEX', '15', 38_200],
  ['NSE:NIFTY50-INDEX', '1m', 6_687],
  ['NSE:NIFTY50-INDEX', '5', 114_524],
  ['NSE:NIFTY50-INDEX', 'D', 1_525],
  ['NSE:NIFTYBANK-INDEX', '1', 476_028],
  ['NSE:NIFTYBANK-INDEX', '15', 31_847],
  ['NSE:NIFTYBANK-INDEX', '1m', 6_685],
  ['NSE:NIFTYBANK-INDEX', '5', 95_443],
  ['NSE:NIFTYBANK-INDEX', 'D', 1_274],
]
const rows = (list: [string, string, number][]) => list.map(([symbol, resolution, barCount]) => ({ symbol, resolution, barCount }))

describe('sessionsOnHand', () => {
  it('counts an index once, at its best-covered resolution, not once per stored range', () => {
    // SENSEX: five ranges, four of them over the same 1,274 days.
    const sensex = rows(RECORDED.filter(([s]) => s === 'BSE:SENSEX-INDEX'))
    expect(sensex.reduce((n, r) => n + estimateSessions(r.resolution, r.barCount), 0)).toBe(5_105)
    expect(sessionsOnHand(sensex)).toEqual({ total: 1_274, indices: 1, longest: 1_274 })
  })

  it('gives about 5,450 index-days for the store that used to read 21,782', () => {
    const all = rows(RECORDED)
    // What the overview showed: every range added up.
    expect(all.reduce((n, r) => n + estimateSessions(r.resolution, r.barCount), 0)).toBe(21_782)
    // BANKEX 9, SENSEX 1,274, FINNIFTY 82, INDIA VIX 1,274, MIDCPNIFTY 11, NIFTY 1,528, BANKNIFTY 1,274.
    expect(sessionsOnHand(all)).toEqual({ total: 5_452, indices: 7, longest: 1_528 })
  })

  it('takes the longer of two ranges at one resolution, however they were stored', () => {
    expect(
      sessionsOnHand(
        rows([
          ['NSE:FINNIFTY-INDEX', '1', 30_649],
          ['NSE:FINNIFTY-INDEX', '1m', 6_373],
        ]),
      ),
    ).toEqual({ total: 82, indices: 1, longest: 82 })
  })

  it('adds the indices together and does not mind the symbol\'s case', () => {
    expect(
      sessionsOnHand(
        rows([
          ['NSE:NIFTY50-INDEX', 'D', 1_525],
          ['nse:nifty50-index', '5', 114_524],
          ['NSE:NIFTYBANK-INDEX', '1', 476_028],
        ]),
      ),
    ).toEqual({ total: 1_527 + 1_270, indices: 2, longest: 1_527 })
  })

  it('is zero with nothing stored, and an empty range is not an index on hand', () => {
    expect(sessionsOnHand([])).toEqual({ total: 0, indices: 0, longest: 0 })
    expect(sessionsOnHand(rows([['NSE:NIFTY50-INDEX', '5', 0]]))).toEqual({ total: 0, indices: 0, longest: 0 })
  })
})
