import { describe, expect, it } from 'vitest'

import {
  SENTINEL_STATUS_COMMAND,
  STALE_HOURS,
  WAIT_TIMEOUT_MS,
  checkupIdParam,
  checkupsQuery,
  countsText,
  emptyToDoText,
  groupItems,
  itemLink,
  pendingText,
  readCheckupLatest,
  readCheckupList,
  slotLabel,
  staleNote,
  stateBadge,
  verdictBadge,
  waitOutcome,
  waitTimeoutMessage,
} from './checkup'
import type { CheckupItem } from './checkup'

/**
 * The Desk checkup page's rules, pinned: what needs a person comes first, a
 * skipped or unreadable check never reads as a passed one, and an old or
 * missing checkup is said out loud.
 */

function item(over: Partial<CheckupItem>): CheckupItem {
  return {
    key: 'x',
    area: 'Desk',
    title: 'Something',
    state: 'ok',
    detail: '',
    action: '',
    link: null,
    ...over,
  }
}

describe('labels', () => {
  it('names every slot the way Sentinel does, and shows an unknown one as written', () => {
    expect(slotLabel('morning')).toBe('Before the open')
    expect(slotLabel('close')).toBe('After the close')
    expect(slotLabel('night')).toBe('End of day')
    expect(slotLabel('weekly')).toBe('Weekly review')
    expect(slotLabel('on-request')).toBe('On request')
    expect(slotLabel('midday')).toBe('midday')
  })

  it('badges a done checkup by its verdict', () => {
    expect(verdictBadge({ status: 'done', verdict: 'ok' })).toEqual({ label: 'All good', tone: 'pos' })
    expect(verdictBadge({ status: 'done', verdict: 'attention' })).toEqual({ label: 'Worth a look', tone: 'warn' })
    expect(verdictBadge({ status: 'done', verdict: 'action' })).toEqual({ label: 'Needs action', tone: 'neg' })
  })

  it('never shows a failed or unfinished checkup as a good one', () => {
    expect(verdictBadge({ status: 'failed', verdict: '' })).toEqual({ label: 'Did not finish', tone: 'neg' })
    // A stale verdict on a failed row still does not make it good.
    expect(verdictBadge({ status: 'failed', verdict: 'ok' }).tone).toBe('neg')
    expect(verdictBadge({ status: 'requested', verdict: '' }).label).toBe('Waiting')
    expect(verdictBadge({ status: 'running', verdict: '' }).label).toBe('Running')
    expect(verdictBadge({ status: 'done', verdict: '' })).toEqual({ label: 'No verdict', tone: 'neutral' })
    expect(verdictBadge({ status: 'done', verdict: 'great' })).toEqual({ label: 'great', tone: 'neutral' })
  })

  it('badges items with the verdict words, and an unknown state as written', () => {
    expect(stateBadge('fail')).toEqual({ label: 'Needs action', tone: 'neg' })
    expect(stateBadge('warn')).toEqual({ label: 'Worth a look', tone: 'warn' })
    expect(stateBadge('skip')).toEqual({ label: 'Not checked', tone: 'neutral' })
    expect(stateBadge('critical')).toEqual({ label: 'critical', tone: 'neutral' })
    expect(stateBadge('').label).toBe('unknown')
  })

  it('says the counts that are not zero, most urgent first', () => {
    expect(countsText({ ok: 9, warn: 2, fail: 1, info: 0, skip: 1 })).toBe('1 to do · 2 to look at · 9 ok · 1 not checked')
    expect(countsText({ ok: 0, warn: 0, fail: 0, info: 0, skip: 0 })).toBe('no items')
    expect(countsText(undefined)).toBe('—')
  })

  it('says whose checkup is pending and whether Sentinel has it yet', () => {
    expect(pendingText({ slot: 'on-request', status: 'requested', requestedBy: 'upendra' })).toBe(
      'A checkup asked for by upendra is waiting for Sentinel to pick it up.',
    )
    expect(pendingText({ slot: 'on-request', status: 'running', requestedBy: 'upendra' })).toBe(
      'A checkup asked for by upendra is running now.',
    )
    expect(pendingText({ slot: 'morning', status: 'running', requestedBy: '' })).toBe(
      'The scheduled checkup (Before the open) is running now.',
    )
  })
})

describe('groupItems', () => {
  it('puts every failure before every warning, keeping the order Sentinel checked them in', () => {
    const items = [
      item({ key: 'w1', state: 'warn' }),
      item({ key: 'ok1', state: 'ok' }),
      item({ key: 'f1', state: 'fail' }),
      item({ key: 'w2', state: 'warn' }),
      item({ key: 'f2', state: 'fail' }),
    ]

    expect(groupItems(items).toDo.map((i) => i.key)).toEqual(['f1', 'f2', 'w1', 'w2'])
  })

  it('shows a state it does not know under information, never as good', () => {
    const groups = groupItems([
      item({ key: 'odd', state: 'critical' }),
      item({ key: 'note', state: 'info' }),
      item({ key: 'fine', state: 'ok' }),
      item({ key: 'holiday', state: 'skip' }),
    ])

    expect(groups.info.map((i) => i.key)).toEqual(['note', 'odd'])
    expect(groups.ok.map((i) => i.key)).toEqual(['fine'])
    expect(groups.skip.map((i) => i.key)).toEqual(['holiday'])
    expect(groups.toDo).toEqual([])
  })

  it('says "nothing to do" only when the report could say so', () => {
    expect(emptyToDoText({ status: 'done', itemsUnreadable: false })).toBe('Nothing to do.')
    expect(emptyToDoText({ status: 'done', itemsUnreadable: true })).not.toContain('Nothing to do')
    expect(emptyToDoText({ status: 'failed', itemsUnreadable: false })).toContain('did not finish')
  })

  it('loses no item', () => {
    const items = ['fail', 'warn', 'info', 'ok', 'skip', 'OK', ''].map((state, i) => item({ key: `k${i}`, state }))
    const g = groupItems(items)

    expect(g.toDo.length + g.info.length + g.ok.length + g.skip.length).toBe(items.length)
  })
})

describe('itemLink', () => {
  it('links console paths only', () => {
    expect(itemLink('/admin/incidents')).toBe('/admin/incidents')
    expect(itemLink(' /admin/broker ')).toBe('/admin/broker')
    expect(itemLink(null)).toBeNull()
    expect(itemLink('')).toBeNull()
    expect(itemLink('admin/incidents')).toBeNull()
    expect(itemLink('//evil.example/x')).toBeNull()
    expect(itemLink('/\\evil.example')).toBeNull()
    expect(itemLink('https://example.com')).toBeNull()
    expect(itemLink('javascript:alert(1)')).toBeNull()
  })
})

describe('staleNote', () => {
  const now = Date.parse('2026-09-28T03:10:00Z')
  const hoursAgo = (h: number) => new Date(now - h * 3_600_000).toISOString()
  const ist = (iso: string) => `IST(${iso})`

  it('says nothing while the page does not know yet', () => {
    expect(staleNote(undefined, now, ist)).toBeNull()
  })

  it('says so when no checkup has ever finished', () => {
    const note = staleNote(null, now, ist)
    expect(note).toContain('No checkup has finished yet')
    expect(note).toContain(SENTINEL_STATUS_COMMAND)
  })

  it(`is quiet inside ${STALE_HOURS} hours and names the last checkup past it`, () => {
    expect(staleNote(hoursAgo(STALE_HOURS - 1), now, ist)).toBeNull()

    const old = hoursAgo(STALE_HOURS + 1)
    expect(staleNote(old, now, ist)).toBe(
      `No checkup since IST(${old}) IST; Sentinel runs them. Check: systemctl status algotrading-sentinel`,
    )
  })

  it('does not invent a date it cannot read', () => {
    expect(staleNote('yesterday', now, ist)).toBeNull()
  })
})

describe('waiting for a checkup asked for now', () => {
  const started = Date.parse('2026-09-28T03:10:00Z')

  it('waits until the checkup asked for, or a later one, has finished', () => {
    const base = { runId: 41, startedMs: started, nowMs: started + 10_000 }
    expect(waitOutcome({ ...base, latestId: null })).toBe('waiting')
    expect(waitOutcome({ ...base, latestId: 40 })).toBe('waiting')
    expect(waitOutcome({ ...base, latestId: 41 })).toBe('done')
    expect(waitOutcome({ ...base, latestId: 42 })).toBe('done')
  })

  it('gives up after three minutes', () => {
    const base = { runId: 41, startedMs: started, latestId: 40 }
    expect(waitOutcome({ ...base, nowMs: started + WAIT_TIMEOUT_MS - 1 })).toBe('waiting')
    expect(waitOutcome({ ...base, nowMs: started + WAIT_TIMEOUT_MS })).toBe('timeout')
    // Finished at the last moment is finished, not timed out.
    expect(waitOutcome({ ...base, latestId: 41, nowMs: started + WAIT_TIMEOUT_MS })).toBe('done')
  })

  it('sends someone to the server only when Sentinel did not pick the request up', () => {
    expect(waitTimeoutMessage('requested')).toBe(
      'Sentinel did not pick it up. On the server: systemctl status algotrading-sentinel',
    )
    expect(waitTimeoutMessage(null)).toBe(waitTimeoutMessage('requested'))
    expect(waitTimeoutMessage('running')).toContain('started the checkup but it has not finished')
  })
})

describe('reading the API', () => {
  it('builds the list query and reads the ?id= of a checkup', () => {
    expect(checkupsQuery(30)).toBe('take=30')
    expect(checkupsQuery(Number.NaN)).toBe('take=30')
    expect(checkupIdParam('12')).toBe(12)
    expect(checkupIdParam(null)).toBeNull()
    expect(checkupIdParam('0')).toBeNull()
    expect(checkupIdParam('-3')).toBeNull()
    expect(checkupIdParam('1e3')).toBeNull()
    expect(checkupIdParam('abc')).toBeNull()
  })

  it('refuses a list that is not a list, rather than showing no history', () => {
    expect(readCheckupList([])).toEqual([])
    expect(() => readCheckupList({ items: [] })).toThrow()
    expect(() => readCheckupList('<!doctype html>')).toThrow()
  })

  it('refuses a latest answer with a key missing: null is an answer, absent is not', () => {
    const body = { latest: null, pending: null, lastCompletedUtc: null }
    expect(readCheckupLatest(body)).toEqual(body)
    expect(() => readCheckupLatest({ latest: null, pending: null })).toThrow()
    expect(() => readCheckupLatest(null)).toThrow()
    expect(() => readCheckupLatest([])).toThrow()
  })
})
