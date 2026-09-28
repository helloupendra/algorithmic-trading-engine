import { describe, expect, it } from 'vitest'

import {
  backendPulse,
  calendarPulse,
  connectorsSummary,
  feedPulses,
  formatUptime,
  headlinePulse,
  heartbeatFeed,
  livePulse,
  marketPulses,
  recapVendors,
} from './pulse'
import type { Pulse } from './pulse'
import type { IngestorStatus, LiveFeed, MarketSessionInfo, Provider } from './types'

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
    expect(pulses.map((p) => p.short)).toEqual(['Open'])
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

describe('holidays', () => {
  // 2026-09-14, Ganesh Chaturthi: NSE shut all day, MCX shut until its evening session.
  const nseHoliday = { ...nseClosed, isHoliday: true, holidayName: 'Ganesh Chaturthi', holidayClosure: 'FullDay' }
  const mcxMorningShut = { ...mcxClosed, isTradingDay: true, isHoliday: true, holidayName: 'Ganesh Chaturthi', holidayClosure: 'MorningSession' }

  it('names the holiday when every market is shut', () => {
    const [pulse] = marketPulses(nseHoliday, mcxMorningShut, [])
    expect(pulse.label).toBe('Holiday · Ganesh Chaturthi')
    expect(pulse.title).toContain('NSE closed for Ganesh Chaturthi')
  })

  it('says why NSE is shut while MCX trades', () => {
    const [pulse] = marketPulses(nseHoliday, mcxOpen, [])
    expect(pulse.label).toBe('MCX open')
    expect(pulse.title).toContain('NSE closed for Ganesh Chaturthi')
  })

  it('raises a missing holiday calendar, and stays quiet when it is loaded', () => {
    const missing = { ...nseClosed, calendarWarning: 'No NSE holiday calendar is loaded for 2027; only weekends are known to be closed.' }
    expect(calendarPulse(missing, mcxOpen)).toMatchObject({ label: 'Holiday calendar missing', tone: 'warn' })
    expect(calendarPulse(nseClosed, mcxOpen)).toBeNull()
    expect(calendarPulse(undefined, undefined)).toBeNull()
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

    // An old heartbeat's "None None" is no reason: the status stands in for it.
    const dropped = feedPulses([truedata], [beat('python-truedata-feed', 3, { status: 'Disconnected', isHealthy: false, lastError: 'None None' })], false, NOW)
    expect(dropped[0].title).toBe('Disconnected · pid 22')
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

describe('connectorsSummary', () => {
  const at = Date.parse('2026-09-15T02:30:00Z') // 08:00 IST
  function provider(key: string, name: string, extra: Partial<Provider>): Provider {
    return {
      key,
      displayName: name,
      kind: 'Data',
      auth: 'OAuthDaily',
      isDataProvider: true,
      isBroker: false,
      isInstalled: true,
      plannedNote: '',
      isConfigured: true,
      session: { isConnected: true, connectedUtc: '2026-09-15T02:15:00Z', ageSeconds: 900, needsReconnect: false, expiresUtc: '2026-09-16T00:30:00Z' },
      ...extra,
    } as Provider
  }
  const fyersP = provider('fyers', 'FYERS', { kind: 'Both', isBroker: true })
  const dhanP = provider('dhan', 'Dhan', { session: { isConnected: true, connectedUtc: '2026-09-15T02:15:00Z', ageSeconds: 900, needsReconnect: false, expiresUtc: '2026-09-16T02:15:00Z' } })
  const truedataP = provider('truedata', 'TrueData', { auth: 'ApiKey', session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: false } })
  const replayP = provider('replay', 'Replay', { auth: 'None', isConfigured: false })
  const feed = (key: string, running: boolean): LiveFeed => ({ key, displayName: key, isRunning: running, managed: running, processId: running ? 1 : null, source: running ? 'managed' : 'none' })

  it('is one quiet pill when every connector that is set up is ready', () => {
    const s = connectorsSummary([fyersP, dhanP, truedataP, replayP], [feed('fyers', false), feed('dhan', true)], true, at)!
    expect(s.pulse).toMatchObject({ label: 'Connectors 3/3 ready', tone: 'pos' })
    expect(s.lines.map((l) => l.key)).toEqual(['fyers', 'dhan', 'truedata'])
    expect(s.lines[0]).toMatchObject({ role: 'Broker + data', feed: 'feed off' })
    expect(s.lines[1].session).toContain('valid until')
    expect(s.lines[2].session).toBe('signs in automatically')
    expect(s.liveFeeds).toEqual(['Dhan'])
  })

  it('names the one connector that needs a sign-in, and is calm on a holiday', () => {
    const expired = provider('dhan', 'Dhan', { session: { isConnected: true, connectedUtc: '2026-09-14T02:15:00Z', ageSeconds: 90000, needsReconnect: true, expiresUtc: '2026-09-15T02:15:00Z' } })
    expect(connectorsSummary([fyersP, expired], [], true, at)!.pulse).toMatchObject({ label: 'Dhan sign-in needed', tone: 'neg' })
    expect(connectorsSummary([fyersP, expired], [], false, at)!.pulse.tone).toBe('idle')
    const never = provider('fyers', 'FYERS', { session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    expect(connectorsSummary([never, expired], [], true, at)!.pulse.label).toBe('2 connectors need sign-in')
  })

  it('calls an unsigned FYERS a backup, not an outage, while Dhan is signed in', () => {
    // 27 Sep: Dhan signed itself in and fed the desk, and the front door still
    // drew "Not linked · FYERS session" in red.
    const fyersOut = provider('fyers', 'FYERS', { kind: 'Both', isBroker: true, session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    const s = connectorsSummary([fyersOut, dhanP, truedataP], [feed('fyers', false), feed('dhan', true), feed('truedata', false)], true, at)!
    expect(s.pulse).toMatchObject({ label: 'FYERS backup not signed in', tone: 'warn' })
    expect(s.pulse.title).toContain('Live data runs on Dhan')
    expect(s.dataOn?.key).toBe('dhan')
    expect(s.backupsDown.map((l) => l.key)).toEqual(['fyers'])
    expect(connectorsSummary([fyersOut, dhanP], [feed('fyers', false), feed('dhan', false)], false, at)!.pulse.tone).toBe('idle')
  })

  it('is still an alarm when nothing that signs in daily is ready, whatever an API-key vendor says', () => {
    const fyersOut = provider('fyers', 'FYERS', { kind: 'Both', session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    const dhanOut = provider('dhan', 'Dhan', { session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    const s = connectorsSummary([fyersOut, dhanOut, truedataP], [feed('fyers', false), feed('dhan', false), feed('truedata', false)], true, at)!
    expect(s.pulse).toMatchObject({ label: '2 connectors need sign-in', tone: 'neg' })
    expect(s.dataOn).toBeNull()
  })

  it('warns when two feeds run at once, and ignores a vendor nobody set up', () => {
    const s = connectorsSummary([fyersP, dhanP], [feed('fyers', true), feed('dhan', true)], true, at)!
    expect(s.pulse).toMatchObject({ label: '2 feeds running', tone: 'warn' })
    const notSetUp = provider('dhan', 'Dhan', { isConfigured: false, session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    expect(connectorsSummary([fyersP, notSetUp], [], true, at)!.pulse).toMatchObject({ label: 'Connectors 1/1 ready', tone: 'pos' })
  })

  it('gives each headline a short form for a narrow bar', () => {
    const expired = provider('dhan', 'Dhan', { session: { isConnected: true, connectedUtc: '2026-09-14T02:15:00Z', ageSeconds: 90000, needsReconnect: true, expiresUtc: '2026-09-15T02:15:00Z' } })
    const fyersOut = provider('fyers', 'FYERS', { kind: 'Both', isBroker: true, session: { isConnected: false, connectedUtc: null, ageSeconds: null, needsReconnect: true } })
    expect(connectorsSummary([fyersP, expired], [], true, at)!.pulse.short).toBe('Dhan sign-in')
    expect(connectorsSummary([fyersOut, expired], [], true, at)!.pulse.short).toBe('2 sign-ins')
    expect(connectorsSummary([fyersOut, dhanP], [feed('fyers', false), feed('dhan', true)], true, at)!.pulse.short).toBe('Backup sign-in')
    expect(connectorsSummary([fyersP, dhanP], [feed('fyers', true), feed('dhan', true)], true, at)!.pulse.short).toBe('2 feeds')
    expect(connectorsSummary([fyersP, dhanP], [], true, at)!.pulse.short).toBe('Ready')
  })
})

describe('backendPulse', () => {
  const status = { uptimeSeconds: 5 * 3600 + 12 * 60, startedUtc: '2026-09-28T01:00:00Z', environment: 'Production' }

  it('says nothing before the first answer', () => {
    expect(backendPulse({ isDown: false, restartedAt: null })).toBeNull()
  })

  it('puts down ahead of everything, even with an old answer on hand', () => {
    expect(backendPulse({ isDown: true, restartedAt: '2026-09-28T05:00:00Z', status })).toMatchObject({ label: 'Backend down', tone: 'neg' })
  })

  it('names a restart in IST, then the uptime once all is well', () => {
    expect(backendPulse({ isDown: false, restartedAt: '2026-09-28T06:10:00Z', status })?.label).toBe('Backend restarted 11:40')
    expect(backendPulse({ isDown: false, restartedAt: null, status })).toMatchObject({
      label: 'Backend up 5h 12m',
      tone: 'pos',
      title: expect.stringMatching(/^Started 28 Sep\w*, 06:30 IST · Production$/),
    })
  })

  it('formats an uptime short enough for the bar', () => {
    expect([48, 360, 7200, 7260].map(formatUptime)).toEqual(['48s', '6m', '2h', '2h 1m'])
  })
})

describe('livePulse', () => {
  it('says Live only while a current hub pushes prices and desk events', () => {
    expect(livePulse('connected', false)).toMatchObject({ label: 'Live', tone: 'live' })
  })

  it('does not call an older API\'s hub Live: its prices are pushed, its fills and stops are not', () => {
    const p = livePulse('legacy', false)
    expect(p).toMatchObject({ label: 'Prices only — fills polled', short: 'Prices only', tone: 'warn' })
    expect(p!.title).toMatch(/older build/)
    expect(livePulse('legacy', true)).toEqual(p)
  })

  it('warns of a dropped connection only once it has lasted', () => {
    expect(livePulse('reconnecting', false)).toBeNull()
    expect(livePulse('disconnected', false)).toBeNull()
    expect(livePulse('reconnecting', true)).toMatchObject({ label: 'Reconnecting', tone: 'warn' })
    expect(livePulse('reconnecting', true)?.title).toMatch(/may be behind the market/)
  })
})

describe('headlinePulse', () => {
  const p = (key: string, tone: Pulse['tone']): Pulse => ({ key, label: key, tone, title: '' })

  it('names the loudest pulse and counts the other alarms', () => {
    const h = headlinePulse([p('backend', 'pos'), p('connectors', 'warn'), p('no-feed', 'neg'), p('calendar', 'warn')])
    expect(h).toEqual({ pulse: p('no-feed', 'neg'), more: 2 })
  })

  it('names the running feed when nothing is wrong', () => {
    expect(headlinePulse([p('backend', 'pos'), p('connectors', 'pos'), p('dhan', 'live')])).toEqual({ pulse: p('dhan', 'live'), more: 0 })
  })

  it("keeps the caller's order on a tie, and says nothing about nothing", () => {
    expect(headlinePulse([p('connectors', 'pos'), p('backend', 'pos')])?.pulse.key).toBe('connectors')
    expect(headlinePulse([])).toBeNull()
  })
})

describe('short market labels for a phone', () => {
  it('names the one market that is open, and shortens the rest', () => {
    expect(marketPulses(nseClosed, mcxOpen, [])[0].short).toBeUndefined()
    expect(marketPulses(nseClosed, mcxClosed, [])[0].short).toBe('Closed')
    expect(marketPulses(nseOpen, mcxOpen, ['TrueData']).map((p) => p.short)).toEqual(['Open', 'Recap'])
  })
})

describe('short labels for the health item', () => {
  // The top bar's health item shows `short` from 1100px down, so every pulse
  // that can head it needs one that fits beside the market and the incidents.
  const status = { uptimeSeconds: 6 * 60, startedUtc: '2026-09-28T01:00:00Z', environment: null }

  it('gives every backend state a word or two', () => {
    expect(backendPulse({ isDown: true, restartedAt: null })?.short).toBe('API down')
    expect(backendPulse({ isDown: false, restartedAt: '2026-09-28T06:10:00Z', status })?.short).toBe('Restarted')
    expect(backendPulse({ isDown: false, restartedAt: null, status })).toMatchObject({ label: 'Backend up 6m', short: 'API up' })
  })

  it('shortens the calendar warning and the feed alarms, keeping the vendor\'s name', () => {
    const missing = { ...nseClosed, calendarWarning: 'No NSE holiday calendar is loaded for 2027.' }
    expect(calendarPulse(missing)?.short).toBe('No calendar')
    expect(feedPulses([fyers], [], false, NOW).map((p) => [p.label, p.short])).toEqual([['FYERS no heartbeat', 'FYERS silent']])
    expect(feedPulses([fyers], [beat('python-live-ingestor', 40, { status: 'Refused' })], false, NOW)[0]).toMatchObject({ label: 'FYERS refused', short: 'FYERS down', tone: 'neg' })
    expect(feedPulses([truedataOff], [], true, NOW)[0]).toMatchObject({ label: 'No feed running', short: 'No feed' })
    // A feed that is simply on keeps its name: it was short already.
    expect(feedPulses([fyers], [beat('python-live-ingestor', 3)], false, NOW)[0].short).toBeUndefined()
  })
})
