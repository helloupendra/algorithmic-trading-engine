import { describe, expect, it } from 'vitest'

import { describeFeedState, feedDiagnostics, feedsView, meaningfulError } from './feeds'
import type { IngestorStatus, LiveFeed } from './types'

/**
 * The feeds panel must never offer Start on a guess.
 *
 * The header of the same page once showed a green Start for ~2 s on a feed
 * that was running, because a query that had not answered yet read as
 * "stopped". These pin the rule for every vendor's row: no control before the
 * first answer, none when the list could not be fetched, and none on an old
 * answer kept on screen after a refresh failed.
 */

const fyers: LiveFeed = {
  key: 'fyers',
  displayName: 'FYERS',
  isRunning: true,
  managed: true,
  processId: 4242,
  source: 'managed',
}

const truedata: LiveFeed = {
  key: 'truedata',
  displayName: 'TrueData',
  isRunning: false,
  managed: false,
  processId: null,
  source: 'none',
}

describe('feedsView', () => {
  it('claims nothing before the API has answered', () => {
    const view = feedsView({ isPending: true, isError: false, error: null, data: undefined })
    expect(view.kind).toBe('checking')
    expect('rows' in view).toBe(false)
  })

  it('offers no feed at all when the first request failed', () => {
    const error = new Error('502')
    const view = feedsView({ isPending: false, isError: true, error, data: undefined })
    expect(view).toEqual({ kind: 'failed', error })
  })

  it('offers Stop on a running feed and Start on a stopped one once answered', () => {
    const view = feedsView({ isPending: false, isError: false, error: null, data: [fyers, truedata] })
    if (view.kind !== 'ready') throw new Error(`expected ready, got ${view.kind}`)
    expect(view.stale).toBe(false)
    expect(view.rows.map((r) => [r.feed.key, r.control])).toEqual([
      ['fyers', 'stop'],
      ['truedata', 'start'],
    ])
  })

  it('offers nothing on a last-known answer after a refresh failed', () => {
    const view = feedsView({ isPending: false, isError: true, error: new Error('timeout'), data: [fyers, truedata] })
    if (view.kind !== 'ready') throw new Error(`expected ready, got ${view.kind}`)
    expect(view.stale).toBe(true)
    expect(view.rows.every((r) => r.control === 'none')).toBe(true)
    expect(view.rows[1].state).toBe('Stopped — last known')
  })
})

describe('describeFeedState', () => {
  it('says how the API holds a running feed', () => {
    expect(describeFeedState(fyers)).toBe('Running (pid 4242)')
    expect(describeFeedState({ ...fyers, managed: false, source: 'adopted' })).toBe('Running (adopted, pid 4242)')
    expect(describeFeedState(truedata)).toBe('Stopped')
  })
})

describe('feedDiagnostics', () => {
  // 2026-09-15 07:50 IST: Dhan running, FYERS stopped, last night's TrueData recap row still in the table.
  const now = Date.parse('2026-09-15T02:20:30Z')
  const beat = (sourceName: string, at: string, extra: Partial<IngestorStatus> = {}): IngestorStatus => ({
    sourceName,
    status: 'Running',
    lastHeartbeatUtc: at,
    lastWatchlistRefreshUtc: at,
    currentSubscribedSymbols: ['A', 'B'],
    lastError: null,
    updatedUtc: at,
    isHealthy: true,
    ...extra,
  })
  const dhan: LiveFeed = { key: 'dhan', displayName: 'Dhan', isRunning: true, managed: true, processId: 222061, source: 'managed' }
  const fyersOff: LiveFeed = { ...fyers, isRunning: false, managed: false, processId: null, source: 'none' }
  const heartbeats = [
    beat('python-dhan-feed', '2026-09-15T02:20:19Z'),
    beat('python-live-ingestor', '2026-09-15T02:17:32Z', { status: 'Disconnected', isHealthy: false, currentSubscribedSymbols: [] }),
    beat('python-truedata-recap', '2026-09-14T09:00:55Z', { status: 'Disconnected', isHealthy: false, lastError: 'None None' }),
  ]

  it('joins each feed to its own heartbeat, never to any healthy one', () => {
    const d = feedDiagnostics([fyersOff, truedata, dhan], heartbeats, now)
    const byKey = Object.fromEntries(d.rows.map((r) => [r.key, r]))
    expect(byKey.dhan).toMatchObject({ process: 'Running (pid 222061)', heartbeat: 'Running', heartbeatTone: 'pos', symbols: 2, note: null })
    // Dhan's healthy heartbeat must not make FYERS look like it runs outside the console.
    expect(byKey.fyers).toMatchObject({ process: 'Stopped', heartbeat: 'Disconnected · quiet', note: null })
    expect(byKey.truedata).toMatchObject({ heartbeat: null, error: null })
  })

  it('moves heartbeats older than half an hour out of the rows', () => {
    const d = feedDiagnostics([fyersOff, truedata, dhan], heartbeats, now)
    expect(d.older.map((o) => o.sourceName)).toEqual(['python-truedata-recap'])
  })

  it('says when a heartbeat arrives with no process behind it', () => {
    const d = feedDiagnostics([fyersOff], [beat('python-live-ingestor', '2026-09-15T02:20:25Z')], now)
    expect(d.rows[0].note).toContain('started outside this console')
  })

  it('drops error text that says nothing', () => {
    expect(meaningfulError('None None')).toBeNull()
    expect(meaningfulError(' null ')).toBeNull()
    expect(meaningfulError('')).toBeNull()
    expect(meaningfulError('DH-901 Invalid_Authentication')).toBe('DH-901 Invalid_Authentication')
  })
})
