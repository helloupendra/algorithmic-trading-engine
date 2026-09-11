import { describe, expect, it } from 'vitest'

import { feedPulses, heartbeatFeed, marketPulses, recapVendors } from './pulse'
import type { IngestorStatus, LiveFeed, MarketSessionInfo } from './types'

/**
 * The pulse row must say what is on, by name.
 *
 * On 2026-09-11 it read "NSE closed · MCX open · Feed live (1)" while TrueData
 * replayed the NSE session and three strategies traded on it: the replay was
 * invisible and the one feed had no name. These pin the replacement rules.
 */

const NOW = Date.parse('2026-09-11T13:45:00Z') // 19:15 IST

function session(isMarketOpen: boolean, closeIst: string, nextOpenUtc: string): MarketSessionInfo {
  return {
    exchange: 'NSE',
    segment: 'CM',
    utcNow: new Date(NOW).toISOString(),
    localNow: '',
    isTradingDay: true,
    isMarketOpen,
    sessionOpenUtc: '2026-09-11T03:45:00Z',
    sessionCloseUtc: closeIst,
    nextMarketOpenUtc: nextOpenUtc,
    timeZoneId: 'Asia/Kolkata',
  }
}

const nseClosed = session(false, '2026-09-11T10:00:00Z', '2026-09-14T03:45:00Z') // Mon 09:15 IST
const nseOpen = session(true, '2026-09-11T10:00:00Z', '2026-09-14T03:45:00Z')
const mcxOpen = session(true, '2026-09-11T18:25:00Z', '2026-09-14T03:30:00Z') // until 23:55 IST
const mcxClosed = session(false, '2026-09-11T18:25:00Z', '2026-09-14T03:30:00Z')

function beat(sourceName: string, ageSeconds: number, extra: Partial<IngestorStatus> = {}): IngestorStatus {
  const at = new Date(NOW - ageSeconds * 1000).toISOString()
  return {
    sourceName,
    status: 'Running',
    lastHeartbeatUtc: at,
    lastWatchlistRefreshUtc: at,
    currentSubscribedSymbols: ['NSE:NIFTY50-INDEX', 'NSE:NIFTYBANK-INDEX'],
    lastError: null,
    updatedUtc: at,
    isHealthy: ageSeconds <= 15,
    ...extra,
  }
}

const fyers: LiveFeed = { key: 'fyers', displayName: 'FYERS', isRunning: true, managed: true, processId: 11, source: 'managed' }
const truedata: LiveFeed = { key: 'truedata', displayName: 'TrueData', isRunning: true, managed: true, processId: 22, source: 'managed' }
const truedataOff: LiveFeed = { ...truedata, isRunning: false, managed: false, processId: null, source: 'none' }

describe('heartbeatFeed', () => {
  it('places the FYERS name that predates the second vendor', () => {
    expect(heartbeatFeed('python-live-ingestor')).toEqual({ key: 'fyers', mode: 'live' })
  })

  it('reads the vendor key and whether it is a replay', () => {
    expect(heartbeatFeed('python-truedata-feed')).toEqual({ key: 'truedata', mode: 'live' })
    expect(heartbeatFeed('python-truedata-recap')).toEqual({ key: 'truedata', mode: 'recap' })
  })

  it('does not guess at a name it does not know', () => {
    expect(heartbeatFeed('chain-poller')).toBeNull()
  })
})

describe('marketPulses', () => {
  it('shows only the market that is trading, with the closed one in the tooltip', () => {
    const [pulse] = marketPulses(nseClosed, mcxOpen, [])
    expect(pulse.label).toBe('MCX open')
    expect(pulse.title).toContain('23:55')
    expect(pulse.title).toContain('NSE closed, opens')
  })

  it('folds two open markets into one pill', () => {
    const pulses = marketPulses(nseOpen, mcxOpen, [])
    expect(pulses.map((p) => p.label)).toEqual(['NSE · MCX open'])
  })

  it('says "Markets closed" once, and only when both sessions have answered', () => {
    expect(marketPulses(nseClosed, mcxClosed, []).map((p) => p.label)).toEqual(['Markets closed'])
    expect(marketPulses(nseClosed, undefined, []).map((p) => p.label)).toEqual(['NSE closed'])
    expect(marketPulses(undefined, undefined, [])).toEqual([])
  })

  it('shows a replay as its own pill, never as the market', () => {
    const pulses = marketPulses(nseClosed, mcxOpen, ['TrueData'])
    expect(pulses.map((p) => p.label)).toEqual(['MCX open', 'NSE recap'])
    expect(pulses[1].title).toContain('TrueData')
  })
})

describe('recapVendors', () => {
  it('names a vendor only while its replay heartbeat is live', () => {
    expect(recapVendors([beat('python-truedata-recap', 4)], [fyers, truedata])).toEqual(['TrueData'])
    expect(recapVendors([beat('python-truedata-recap', 600)], [fyers, truedata])).toEqual([])
    expect(recapVendors(undefined, [fyers, truedata])).toEqual([])
  })

  it('lets the newest row speak when an older one from the afternoon is still stored', () => {
    const rows = [beat('python-truedata-recap', 4), beat('python-truedata-feed', 9000)]
    expect(recapVendors(rows, [truedata])).toEqual(['TrueData'])
  })
})

describe('feedPulses', () => {
  it('names each running feed and says which one is a replay', () => {
    const pulses = feedPulses([fyers, truedata], [beat('python-live-ingestor', 3), beat('python-truedata-recap', 5)], false, NOW)
    expect(pulses.map((p) => [p.label, p.tone])).toEqual([
      ['FYERS feed', 'live'],
      ['TrueData recap', 'live'],
    ])
    expect(pulses[0].title).toContain('pid 11')
  })

  it('gives a stopped feed no pill', () => {
    const pulses = feedPulses([fyers, truedataOff], [beat('python-live-ingestor', 3), beat('python-truedata-feed', 9000)], false, NOW)
    expect(pulses.map((p) => p.label)).toEqual(['FYERS feed'])
  })

  it('warns about a running feed that is not reporting, rather than calling it live', () => {
    const silent = feedPulses([truedata], [beat('python-truedata-feed', 90)], false, NOW)
    expect(silent.map((p) => [p.label, p.tone])).toEqual([['TrueData no heartbeat', 'warn']])

    const fresh = feedPulses([truedata], [], false, NOW)
    expect(fresh[0].title).toContain('has not reported yet')

    const refused = feedPulses([truedata], [beat('python-truedata-feed', 3, { status: 'Refused', isHealthy: false, lastError: 'User Already Connected' })], false, NOW)
    expect(refused.map((p) => [p.label, p.tone, p.title])).toEqual([['TrueData refused', 'neg', 'User Already Connected · pid 22']])
  })

  it('flags a live heartbeat the API has no process for', () => {
    const pulses = feedPulses([truedataOff], [beat('python-truedata-feed', 3)], false, NOW)
    expect(pulses.map((p) => [p.label, p.tone])).toEqual([['TrueData feed', 'warn']])
  })

  it('raises "No feed running" only while NSE is open', () => {
    expect(feedPulses([truedataOff], [], true, NOW).map((p) => [p.label, p.tone])).toEqual([['No feed running', 'neg']])
    expect(feedPulses([truedataOff], [], false, NOW)).toEqual([])
  })

  it('claims nothing before the feed list has answered', () => {
    expect(feedPulses(undefined, [beat('python-live-ingestor', 3)], true, NOW)).toEqual([])
  })
})
