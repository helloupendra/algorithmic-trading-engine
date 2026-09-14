import { describe, expect, it } from 'vitest'

import { USAGE_ITEMS, marketsLine, usageBadge, usageView } from './usage'
import type { ProviderUsage } from './types'

/**
 * The connector page's "What we take" panel must never claim Off for an
 * answer it does not have: not before the first response, not after a failed
 * one, and not from a stale one.
 */

const usage: ProviderUsage = {
  providerKey: 'acme',
  checkedUtc: '2026-09-14T12:30:00Z',
  markets: { nse: { open: false, holiday: 'Ganesh Chaturthi' }, mcx: { open: true, holiday: null } },
  items: [
    { id: 'session', label: 'Session', offered: true, state: 'on', summary: 'no login needed', lastUtc: null },
    { id: 'liveTicks', label: 'Live ticks', offered: true, state: 'idle', summary: 'running', lastUtc: null },
    { id: 'quotes', label: 'Quotes', offered: true, state: 'off', summary: 'feed stopped', lastUtc: null },
    { id: 'greeks', label: 'Greeks', offered: false, state: 'not-offered', summary: 'not offered', lastUtc: null },
  ],
}

describe('usageBadge', () => {
  it('maps each state to the tone the page uses', () => {
    expect(usageBadge('on')).toEqual({ label: 'On', tone: 'pos' })
    expect(usageBadge('idle')).toEqual({ label: 'Idle', tone: 'neutral' })
    expect(usageBadge('off')).toEqual({ label: 'Off', tone: 'warn' })
    expect(usageBadge('unknown')).toEqual({ label: 'Unknown', tone: 'warn' })
    expect(usageBadge('not-offered')).toEqual({ label: 'Not offered', tone: 'muted' })
  })

  it('treats a state it does not know as unknown, not off', () => {
    expect(usageBadge('paused' as never)).toEqual({ label: 'Unknown', tone: 'warn' })
  })
})

describe('usageView', () => {
  it('shows Checking… on every row before the first answer', () => {
    const view = usageView({ isPending: true, isError: false, error: null, data: undefined })

    expect(view.kind).toBe('checking')
    expect(view.rows.map((r) => r.id)).toEqual(USAGE_ITEMS.map((i) => i.id))
    expect(view.rows.every((r) => r.badge.label === 'Checking…')).toBe(true)
  })

  it('shows Unknown, never Off, when the first request fails', () => {
    const error = new Error('502')
    const view = usageView({ isPending: false, isError: true, error, data: undefined })

    expect(view.kind).toBe('failed')
    expect(view.rows.every((r) => r.badge.label === 'Unknown')).toBe(true)
  })

  it('renders the answer as given', () => {
    const view = usageView({ isPending: false, isError: false, error: null, data: usage })

    expect(view.kind).toBe('ready')
    if (view.kind !== 'ready') return
    expect(view.stale).toBe(false)
    expect(view.rows.map((r) => r.badge.label)).toEqual(['On', 'Idle', 'Off', 'Not offered'])
    expect(view.markets).toBe('NSE closed for Ganesh Chaturthi · MCX open')
  })

  it('marks a kept answer as last known when a refresh fails, except what is merely declared', () => {
    const view = usageView({ isPending: false, isError: true, error: new Error('timeout'), data: usage })

    if (view.kind !== 'ready') throw new Error('expected the last answer to stay on screen')
    expect(view.stale).toBe(true)
    expect(view.rows[0].badge).toEqual({ label: 'On (last known)', tone: 'warn' })
    expect(view.rows[3].badge).toEqual({ label: 'Not offered', tone: 'muted' })
  })
})

describe('marketsLine', () => {
  it('says when a market session could not be read', () => {
    expect(marketsLine({ nse: { open: true, holiday: null }, mcx: { open: null, holiday: null } })).toBe(
      'NSE open · MCX hours unknown',
    )
  })
})
