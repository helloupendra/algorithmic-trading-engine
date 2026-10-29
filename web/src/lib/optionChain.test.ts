import { describe, expect, it } from 'vitest'

import {
  CHAIN_VIEWS,
  chainCsv,
  chainCsvName,
  chainUnderlying,
  chainViewUrl,
  compactIndian,
  compactSigned,
  describeFreshness,
  capturedExpiry,
  expiryLabel,
  isCallItm,
  isPutItm,
  applyTicksToChain,
  classifyBuildUp,
  pointsVsSpot,
  positionMarker,
  positionsBySymbol,
  replayAsOfUtc,
  signedPercent,
  spotMarkerIndex,
  strikeWindow,
} from './optionChain'
import type { OptionChain, OptionChainHeader } from './types'

/**
 * The advanced option chain's reading rules. The owner's complaint about the
 * old page was that you could not tell where the price was; these are the
 * rules that put the spot line, the ITM shading and the freshness line in the
 * right place.
 */

describe('compactIndian', () => {
  it('reads the way Indian chains are written', () => {
    expect(compactIndian(194_000)).toBe('1.94L')
    expect(compactIndian(44_000)).toBe('44.0K')
    expect(compactIndian(1_949_000)).toBe('19.49L')
    expect(compactIndian(12_345_678)).toBe('1.23Cr')
    expect(compactIndian(950)).toBe('950')
    expect(compactIndian(0)).toBe('0')
  })

  it('keeps the sign and never invents a number for nothing', () => {
    expect(compactIndian(-7_000)).toBe('−7.0K')
    expect(compactSigned(3_000)).toBe('+3.0K')
    expect(compactSigned(-23_000)).toBe('−23.0K')
    expect(compactSigned(0)).toBe('0')
    expect(compactIndian(null)).toBe('—')
    expect(compactSigned(undefined)).toBe('—')
  })

  it('signs percentages with a true minus and no sign on zero', () => {
    expect(signedPercent(0.11)).toBe('+0.11%')
    expect(signedPercent(-5.82)).toBe('−5.82%')
    expect(signedPercent(0.001)).toBe('0.00%')
  })
})

describe('pointsVsSpot', () => {
  it('reads a level against the spot in whole points, signed with a true minus', () => {
    expect(pointsVsSpot(23_250, 23_140.5)).toBe('+110 vs spot')
    expect(pointsVsSpot(23_100, 23_140.5)).toBe('−41 vs spot')
    expect(pointsVsSpot(83_100, 81_845.6)).toBe('+1,254 vs spot')
  })

  it('says "at the spot" inside half a point', () => {
    expect(pointsVsSpot(23_150, 23_149.7)).toBe('at the spot')
  })

  it('leaves the line out when either side is unknown', () => {
    expect(pointsVsSpot(null, 23_140.5)).toBeNull()
    expect(pointsVsSpot(23_250, null)).toBeNull()
    expect(pointsVsSpot(23_250, 0)).toBeNull()
    expect(pointsVsSpot(Number.NaN, 23_140.5)).toBeNull()
  })
})

describe('in the money', () => {
  it('shades calls below the spot and puts above it', () => {
    const spot = 57_369.65
    expect(isCallItm(57_300, spot)).toBe(true)
    expect(isCallItm(57_400, spot)).toBe(false)
    expect(isPutItm(57_400, spot)).toBe(true)
    expect(isPutItm(57_300, spot)).toBe(false)
  })

  it('shades nothing when the spot is unknown', () => {
    expect(isCallItm(100, null)).toBe(false)
    expect(isPutItm(100, 0)).toBe(false)
  })
})

describe('spotMarkerIndex', () => {
  const strikes = [57_200, 57_300, 57_400, 57_500]

  it('puts the line between the two strikes the spot lies between', () => {
    // Rows 0 and 1 (57,200 and 57,300) are above the line; the line goes before row 2.
    expect(spotMarkerIndex(strikes, 57_369.65)).toBe(2)
  })

  it('puts a spot sitting on a strike just below that strike', () => {
    expect(spotMarkerIndex(strikes, 57_400)).toBe(3)
  })

  it('pins the line to an edge when the spot is outside the shown strikes', () => {
    expect(spotMarkerIndex(strikes, 57_000)).toBe(0)
    expect(spotMarkerIndex(strikes, 58_000)).toBe(4)
  })

  it('draws no line without a spot or strikes', () => {
    expect(spotMarkerIndex(strikes, null)).toBeNull()
    expect(spotMarkerIndex([], 57_369)).toBeNull()
  })
})

describe('strikeWindow', () => {
  const strikes = Array.from({ length: 101 }, (_, i) => 50_000 + i * 100) // 50,000 … 60,000

  it('shows N strikes either side of the ATM', () => {
    const { start, end } = strikeWindow(strikes, 57_400, 10)
    expect(end - start).toBe(21)
    expect(strikes[start]).toBe(56_400)
    expect(strikes[end - 1]).toBe(58_400)
  })

  it('slides inward at the ends so the row count holds', () => {
    const low = strikeWindow(strikes, 50_100, 10)
    expect(low).toEqual({ start: 0, end: 21 })
    const high = strikeWindow(strikes, 60_000, 20)
    expect(high).toEqual({ start: 60, end: 101 })
  })

  it('centres on the nearest strike when the ATM is not in the list', () => {
    const { start } = strikeWindow(strikes, 57_449, 10)
    expect(strikes[start + 10]).toBe(57_400)
  })

  it('shows everything for All or for a short chain', () => {
    expect(strikeWindow(strikes, 57_400, 'all')).toEqual({ start: 0, end: 101 })
    expect(strikeWindow([1, 2, 3], 2, 10)).toEqual({ start: 0, end: 3 })
  })
})

describe('capturedExpiry', () => {
  it("drops the default date the API names when nothing is captured, and keeps a real expiry", () => {
    expect(capturedExpiry('0001-01-01')).toBeNull()
    expect(capturedExpiry('0001-01-01T00:00:00')).toBeNull()
    expect(capturedExpiry('')).toBeNull()
    expect(capturedExpiry(null)).toBeNull()
    expect(capturedExpiry(undefined)).toBeNull()
    expect(capturedExpiry('2026-09-30')).toBe('2026-09-30')
  })
})

describe('expiryLabel', () => {
  it('names the day and how far away it is', () => {
    expect(expiryLabel('2026-09-29', '2026-09-14')).toBe('29 Sep 2026 (+15 days)')
    expect(expiryLabel('2026-09-15', '2026-09-14')).toBe('15 Sep 2026 (+1 day)')
    expect(expiryLabel('2026-09-14', '2026-09-14')).toBe('14 Sep 2026 (today)')
    expect(expiryLabel('2026-09-08', '2026-09-14')).toBe('8 Sep 2026 (expired)')
  })
})

describe('describeFreshness', () => {
  const base: OptionChainHeader = {
    mode: 'live',
    serverUtc: '2026-09-15T05:11:00Z',
    exchange: 'NSE',
    marketOpen: true,
    spot: null,
    spotIsFuture: false,
    future: null,
    vix: null,
    atTheMoneyStrike: null,
    maxPainStrike: null,
    putCallRatio: null,
    putCallRatioOfChange: null,
    supportStrike: null,
    supportOpenInterest: null,
    resistanceStrike: null,
    resistanceOpenInterest: null,
    totalCallOpenInterest: 0,
    totalPutOpenInterest: 0,
    totalCallOpenInterestChange: 0,
    totalPutOpenInterestChange: 0,
    atTheMoneyIv: null,
    daysToExpiry: null,
    lotSize: null,
    lotSizeSource: null,
    spotBetweenLower: null,
    spotBetweenUpper: null,
    snapshotCapturedUtc: '2026-09-15T05:10:30Z',
    snapshotSourceKey: 'dhan',
    liveOverlayUtc: '2026-09-15T05:10:58Z',
    liveSourceKey: 'dhan',
    liveLegs: 22,
    totalLegs: 400,
    freshSeconds: 120,
  }
  const at = (iso: string) => Date.parse(iso)

  it('says live, how long ago and from where', () => {
    const f = describeFreshness(base, at('2026-09-15T05:11:00Z'))
    expect(f.tone).toBe('live')
    expect(f.text).toMatch(/^live · updated 2 s ago · Dhan/)
  })

  it('never calls a live answer live once its newest quote has aged out', () => {
    const f = describeFreshness(base, at('2026-09-15T05:14:00Z'))
    expect(f.tone).toBe('stale')
    expect(f.text).not.toMatch(/^live/)
  })

  it('shows a snapshot with its time and age when no quote is fresh', () => {
    const f = describeFreshness({ ...base, mode: 'snapshot', liveOverlayUtc: null, liveSourceKey: null }, at('2026-09-15T05:11:30Z'))
    expect(f.tone).toBe('snapshot')
    expect(f.text).toBe('snapshot 10:40 IST (1 min old) · Dhan · no live quotes')
  })

  it('warns when the snapshot is several minutes old during the session', () => {
    const f = describeFreshness({ ...base, mode: 'snapshot', liveOverlayUtc: null }, at('2026-09-15T05:25:00Z'))
    expect(f.tone).toBe('stale')
    expect(f.text).toMatch(/recorder may be behind/)
  })

  it('says the market is closed and when the last capture was', () => {
    const f = describeFreshness({ ...base, marketOpen: false, snapshotCapturedUtc: '2026-09-14T12:54:18Z' }, at('2026-09-14T16:00:00Z'))
    expect(f.tone).toBe('closed')
    expect(f.text).toBe('market closed — last capture 14 Sep 18:24 IST · Dhan')
  })

  it('labels a replay as a replay', () => {
    const f = describeFreshness({ ...base, mode: 'replay', marketOpen: false }, at('2026-09-15T08:00:00Z'))
    expect(f.tone).toBe('replay')
    expect(f.text).toMatch(/^replay · snapshot 15 Sep 10:40 IST/)
  })
})

describe('positions on the chain', () => {
  const long = (symbol: string, quantity: number) => ({ symbol, direction: 'LONG', quantity })
  const short = (symbol: string, quantity: number) => ({ symbol, direction: 'SHORT', quantity })

  it('groups open positions by contract', () => {
    const map = positionsBySymbol([long('MCX:CRUDEOIL26SEP9700CE', 1), short('MCX:CRUDEOIL26SEP9700CE', 2), long('X', 1)])
    expect(map.get('MCX:CRUDEOIL26SEP9700CE')).toHaveLength(2)
    expect(map.get('X')).toHaveLength(1)
    expect(positionsBySymbol(null).size).toBe(0)
  })

  it('marks bought and sold lots per side, never netted', () => {
    expect(positionMarker([long('A', 1)])).toEqual({ label: 'B 1', tone: 'long' })
    expect(positionMarker([short('A', 2), short('A', 1)])).toEqual({ label: 'S 3', tone: 'short' })
    // A strategy's short and a manual long on one contract: both shown.
    expect(positionMarker([short('A', 1), long('A', 1)])).toEqual({ label: 'B 1 · S 1', tone: 'mixed' })
    expect(positionMarker([])).toBeNull()
    expect(positionMarker(undefined)).toBeNull()
  })
})

describe('pushed ticks on the chain', () => {
  const now = '2026-09-15T04:00:10.000Z'
  function chain(mode = 'live'): OptionChain {
    const leg = (symbol: string, ltp: number, change: number, oi: number, baseline: number) => ({
      symbol, lastTradedPrice: ltp, priceChange: change, priceChangePercent: null, bidPrice: ltp - 0.5, askPrice: ltp + 0.5,
      volume: 1000, openInterest: oi, openInterestChange: oi - baseline, openInterestChangePercent: null,
      impliedVolatility: 14, delta: 0.5, buildUp: 'Neutral', openInterestBaseline: baseline, isLive: false, quoteUpdatedUtc: null,
    })
    return {
      underlying: 'NIFTY', expiryDate: '2026-09-22', asOfUtc: now, spotPrice: 23500, atTheMoneyStrike: 23500,
      maxPainStrike: null, putCallRatio: null, totalCallOpenInterest: 0, totalPutOpenInterest: 0,
      heaviestCallStrike: null, heaviestPutStrike: null, openInterestUnavailable: false,
      strikes: [{ strikePrice: 23500, isAtTheMoney: true, putCallRatio: null, putCallRatioOfChange: null, call: leg('CE1', 100, 10, 50_000, 40_000), put: leg('PE1', 90, -5, 60_000, 61_000) }],
      header: {
        mode, serverUtc: '2026-09-15T04:00:00.000Z', marketOpen: true, snapshotCapturedUtc: '2026-09-15T03:59:30.000Z',
        liveOverlayUtc: null, liveLegs: 0, totalLegs: 2, freshSeconds: 120,
        spot: { symbol: 'NSE:NIFTY50-INDEX', lastPrice: 23500, change: 0, changePercent: 0, previousClose: 23400, previousCloseBasis: 'feed', asOfUtc: null, sourceKey: 'dhan', isLive: false, basis: 'snapshot' },
        future: null, vix: null,
      } as unknown as OptionChainHeader,
    }
  }

  it('moves a leg the way the API overlay does', () => {
    const next = applyTicksToChain(chain(), [{ symbol: 'CE1', lastTradedPrice: 104, bidPrice: 103.9, askPrice: 104.1, volume: 1500, openInterest: 52_000 }], now)
    const call = next.strikes[0].call!
    expect(call.lastTradedPrice).toBe(104)
    expect(call.priceChange).toBe(14) // baseline 100 - 10 = 90
    expect(call.openInterestChange).toBe(12_000)
    expect(call.buildUp).toBe('LongBuildUp')
    expect(call.isLive).toBe(true)
    expect(next.header!.liveOverlayUtc).toBe(now)
    expect(next.header!.liveLegs).toBe(1)
    expect(next.strikes[0].put!.lastTradedPrice).toBe(90) // a put with no push keeps its capture
  })

  it('updates the spot from its previous close and leaves unrelated pushes alone', () => {
    const base = chain()
    const next = applyTicksToChain(base, [{ symbol: 'NSE:NIFTY50-INDEX', lastTradedPrice: 23517 }], now)
    expect(next.header!.spot!.lastPrice).toBe(23517)
    expect(next.header!.spot!.change).toBe(117)
    expect(next.spotPrice).toBe(23517)
    expect(applyTicksToChain(base, [{ symbol: 'OTHER', lastTradedPrice: 5 }], now)).toBe(base)
  })

  it('never touches a replay, and refuses an OI eight times away from a fresh capture', () => {
    const replay = chain('replay')
    expect(applyTicksToChain(replay, [{ symbol: 'CE1', lastTradedPrice: 120 }], now)).toBe(replay)
    const next = applyTicksToChain(chain(), [{ symbol: 'CE1', lastTradedPrice: 101, openInterest: 50_000 * 65 }], now)
    expect(next.strikes[0].call!.openInterest).toBe(50_000)
  })

  it('classifies build-up like the server', () => {
    expect(classifyBuildUp(1, 1)).toBe('LongBuildUp')
    expect(classifyBuildUp(-1, 1)).toBe('ShortBuildUp')
    expect(classifyBuildUp(1, -1)).toBe('ShortCovering')
    expect(classifyBuildUp(-1, -1)).toBe('LongUnwinding')
    expect(classifyBuildUp(0, 5)).toBe('Neutral')
  })
})

describe('the chain page views', () => {
  it('reads the underlying from the URL, falling back to NIFTY', () => {
    expect(chainUnderlying('banknifty')).toBe('BANKNIFTY')
    expect(chainUnderlying(' CRUDEOIL ')).toBe('CRUDEOIL')
    expect(chainUnderlying(null)).toBe('NIFTY')
    expect(chainUnderlying('')).toBe('NIFTY')
  })

  it('accepts a name the instrument master knows, and only a plausible one before it has answered', () => {
    const known = ['NIFTY', 'RELIANCE', 'M&M', 'BAJAJ-AUTO']
    expect(chainUnderlying('RELIANCE', known)).toBe('RELIANCE')
    expect(chainUnderlying('m&m', known)).toBe('M&M')
    expect(chainUnderlying('HDFCBANK', known)).toBe('NIFTY')
    // Not answered yet: a link to a real-looking name opens that chain rather than NIFTY and then a swap.
    expect(chainUnderlying('RELIANCE')).toBe('RELIANCE')
    expect(chainUnderlying('bajaj-auto')).toBe('BAJAJ-AUTO')
    expect(chainUnderlying('not a name')).toBe('NIFTY')
    expect(chainUnderlying('<script>')).toBe('NIFTY')
  })

  it('carries the underlying and expiry between views', () => {
    expect(chainViewUrl('chain', new URLSearchParams('u=BANKNIFTY'))).toBe('/markets/chain?u=BANKNIFTY')
    expect(chainViewUrl('levels', '')).toBe('/markets/chain/levels')
  })

  it('carries the replay clock to OI history and back, and leaves it behind on Levels', () => {
    // A chain rewound to 10:41 must open OI history ending at 10:41, not at now.
    expect(chainViewUrl('oi', '?u=SENSEX&expiry=2026-10-01&at=10:41')).toBe('/markets/chain/oi?u=SENSEX&expiry=2026-10-01&at=10%3A41')
    expect(chainViewUrl('chain', '?u=SENSEX&at=10:41')).toBe('/markets/chain?u=SENSEX&at=10%3A41')
    expect(chainViewUrl('levels', '?u=SENSEX&expiry=2026-10-01&at=10:41')).toBe('/markets/chain/levels?u=SENSEX&expiry=2026-10-01')
  })

  it('has one path per view, all under the chain tab', () => {
    expect(CHAIN_VIEWS.map((v) => v.path)).toEqual(['/markets/chain', '/markets/chain/oi', '/markets/chain/levels'])
  })
})


describe('replayAsOfUtc', () => {
  it('reads the clock as IST on the session day, to the end of the minute', () => {
    // 11:00 IST is 05:30 UTC; the capture taken during 11:00 is in, 11:01's is not.
    expect(replayAsOfUtc('2026-10-29', '11:00')).toBe('2026-10-29T05:30:59.000Z')
    // Before 05:30 IST is the previous UTC day.
    expect(replayAsOfUtc('2026-10-29', '05:15')).toBe('2026-10-28T23:45:59.000Z')
  })

  it('reads anything that is not a day and an hh:mm as live', () => {
    expect(replayAsOfUtc('', '11:00')).toBeUndefined()
    expect(replayAsOfUtc('2026-10-29', '')).toBeUndefined()
    expect(replayAsOfUtc('2026-10-29', '11:00:30')).toBeUndefined()
    expect(replayAsOfUtc('29/10/2026', '11:00')).toBeUndefined()
    expect(replayAsOfUtc('2026-13-45', '11:00')).toBeUndefined()
  })
})

describe('chainCsv', () => {
  const leg = (symbol: string, ltp: number, oi: number) => ({
    symbol, lastTradedPrice: ltp, priceChange: 4, priceChangePercent: 4.17, bidPrice: ltp - 0.5, askPrice: ltp + 0.5,
    volume: 1_200, openInterest: oi, openInterestChange: -7_000, openInterestChangePercent: -3.5,
    impliedVolatility: 13.2, delta: 0.52, gamma: 0.0011, theta: -11.4, vega: 12.9, buildUp: 'ShortCovering',
    isLive: true, quoteUpdatedUtc: '2026-10-29T05:29:58Z',
  })
  const strike = (strikePrice: number, atm = false) => ({
    strikePrice, isAtTheMoney: atm, putCallRatio: 1.25, putCallRatioOfChange: null,
    call: leg(`NSE:NIFTY26N10${strikePrice}CE`, 100, 194_000), put: null,
  })
  const chain = {
    underlying: 'NIFTY', expiryDate: '2026-11-10', asOfUtc: '2026-10-29T05:30:59Z', spotPrice: 24510, atTheMoneyStrike: 24500,
    maxPainStrike: null, putCallRatio: null, totalCallOpenInterest: 0, totalPutOpenInterest: 0,
    heaviestCallStrike: null, heaviestPutStrike: null, openInterestUnavailable: false,
    // Sixty strikes: more than any window the table shows.
    strikes: Array.from({ length: 60 }, (_, i) => strike(23_000 + i * 50, 23_000 + i * 50 === 24_500)),
  } as OptionChain

  const lines = chainCsv(chain).split('\r\n')

  it('writes every strike the chain holds, not the window on screen', () => {
    // A header, sixty strikes and the empty string after the last CRLF.
    expect(lines).toHaveLength(62)
    expect(lines[61]).toBe('')
  })

  it('heads the columns for a spreadsheet: the strike first, then each side in the same order', () => {
    const header = lines[0].split(',')
    expect(header.slice(0, 6)).toEqual(['Underlying', 'Expiry', 'As of (IST)', 'Strike', 'ATM', 'PCR'])
    const call = header.filter((h) => h.startsWith('Call ')).map((h) => h.slice(5))
    const put = header.filter((h) => h.startsWith('Put ')).map((h) => h.slice(4))
    expect(call).toEqual(put)
    expect(call.slice(0, 3)).toEqual(['symbol', 'LTP', 'change %'])
  })

  it('writes numbers as numbers, times in IST, the build-up by its name and a missing leg as empty', () => {
    const atm = lines.find((l) => l.includes(',24500,'))!.split(',')
    expect(atm.slice(0, 6)).toEqual(['NIFTY', '2026-11-10', '2026-10-29T11:00:59+05:30', '24500', 'ATM', '1.25'])
    expect(atm.slice(6, 22)).toEqual([
      'NSE:NIFTY26N1024500CE', '100', '4.17', '99.5', '100.5', '1200', '194000', '-7000', '-3.5',
      '13.2', '0.52', '0.0011', '-11.4', '12.9', 'Short cover', '2026-10-29T10:59:58+05:30',
    ])
    expect(atm.slice(22)).toEqual(Array(16).fill(''))
  })

  it('names the file by the underlying, the expiry and the moment', () => {
    expect(chainCsvName(chain)).toBe('NIFTY-chain-2026-11-10-2026-10-29-1100.csv')
    // An empty chain names .NET's default date as its expiry: there is none.
    expect(chainCsvName({ ...chain, expiryDate: '0001-01-01' })).toBe('NIFTY-chain-2026-10-29-1100.csv')
  })
})
