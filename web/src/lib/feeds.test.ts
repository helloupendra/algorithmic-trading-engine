import { describe, expect, it } from 'vitest'

import { describeFeedState, feedsView } from './feeds'
import type { LiveFeed } from './types'

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
