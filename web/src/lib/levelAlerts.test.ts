import { describe, expect, it } from 'vitest'

import {
  EMPTY_FORM,
  alertLink,
  alertsBadge,
  conditionsFor,
  deliveryLine,
  formFromLink,
  formProblems,
  formToRequest,
  formatLevel,
  parseLevel,
  readingLine,
  rearmAt,
  ruleToForm,
  sideNote,
  statusLine,
} from './levelAlerts'
import type { LevelAlertRule, LevelReading } from './levelAlerts'

const DEFAULTS = { price: 0.2, pcr: 0.05 }

const reading = (value: number | null, over: Partial<LevelReading> = {}): LevelReading => ({
  kind: 'price',
  target: 'NSE:NIFTY50-INDEX',
  displayName: 'NIFTY',
  exchange: 'NSE',
  inSession: true,
  value,
  asOfUtc: '2026-10-30T04:30:00Z',
  state: 'live',
  detail: null,
  context: null,
  ...over,
})

const rule: LevelAlertRule = {
  id: 7,
  userId: 3,
  ownerUserName: 'asha',
  kind: 'price',
  target: 'NSE:NIFTY50-INDEX',
  displayName: 'NIFTY',
  condition: 'above',
  level: 24800,
  description: 'NIFTY crosses above 24,800',
  repeat: true,
  rearmBy: 0.2,
  rearmUnit: 'percent',
  note: 'breakout',
  expiresOn: '2026-11-03',
  notify: true,
  telegramApplies: false,
  status: 'fired',
  armed: false,
  lastFiredUtc: '2026-10-30T04:31:00Z',
  fireCount: 1,
  createdUtc: '2026-10-29T10:00:00Z',
  updatedUtc: '2026-10-29T10:00:00Z',
  now: null,
  side: null,
}

describe('the form', () => {
  it('offers touch for a price and only above or below for a PCR', () => {
    expect(conditionsFor('price').map((c) => c.value)).toEqual(['above', 'below', 'touch'])
    expect(conditionsFor('pcr').map((c) => c.label)).toEqual(['Goes above', 'Goes below'])
  })

  it('opens filled in from the chain or the watchlist, and empty from a bare link', () => {
    expect(formFromLink(new URLSearchParams('kind=pcr&target=nifty&level=1.3216'))).toMatchObject({ kind: 'pcr', target: 'NIFTY', level: '1.32' })
    expect(formFromLink(new URLSearchParams('target=NSE:SBIN-EQ&level=801.5'))).toMatchObject({ kind: 'price', target: 'NSE:SBIN-EQ', level: '801.5' })
    expect(formFromLink(new URLSearchParams('target=NSE:SBIN-EQ&level=abc'))?.level).toBe('')
    expect(formFromLink(new URLSearchParams(''))).toBeNull()
    expect(alertLink('pcr', 'NIFTY', 1.3216)).toBe('/markets/alerts?kind=pcr&target=NIFTY&level=1.32')
    expect(alertLink('price', 'NSE:SBIN-EQ', null)).toBe('/markets/alerts?kind=price&target=NSE%3ASBIN-EQ')
  })

  it('reads a level written the way traders write it, and nothing that is not one', () => {
    expect(parseLevel('24,800')).toBe(24800)
    expect(parseLevel(' 1.3 ')).toBe(1.3)
    expect(parseLevel('')).toBeNull()
    expect(parseLevel('-5')).toBeNull()
    expect(parseLevel('0')).toBeNull()
    expect(parseLevel('abc')).toBeNull()
  })

  it('says what is missing before it sends, and sends what the API expects', () => {
    expect(formProblems(EMPTY_FORM)).toEqual(['Pick a symbol.', 'The level must be a number above zero.'])
    expect(formProblems({ ...EMPTY_FORM, kind: 'pcr', target: 'NIFTY', level: '1.3', condition: 'touch' })).toEqual([
      'A PCR rule goes above or below a level.',
    ])

    const request = formToRequest({ ...EMPTY_FORM, target: ' nse:sbin-eq ', level: '801.50', note: '  ', repeat: false, rearmBy: '1' })
    expect(request).toEqual({
      kind: 'price', target: 'NSE:SBIN-EQ', condition: 'above', level: 801.5, repeat: false,
      rearmBy: null, note: null, expiresOn: null, notify: true, active: true,
    })
  })

  it('edits a fired rule as one to arm again, and a paused one as paused', () => {
    expect(ruleToForm(rule)).toMatchObject({ id: 7, level: '24800', rearmBy: '0.2', note: 'breakout', expiresOn: '2026-11-03', active: true })
    expect(ruleToForm({ ...rule, status: 'paused' }).active).toBe(false)
  })
})

describe('what the page says about a level', () => {
  it('names where a repeating rule arms again, in the value’s own units', () => {
    expect(rearmAt('price', 'above', 24800, null, DEFAULTS)).toBe('it is back at or below 24,750.4')
    expect(rearmAt('price', 'below', 24800, 1, DEFAULTS)).toBe('it is back at or above 25,048')
    expect(rearmAt('pcr', 'above', 1.3, null, DEFAULTS)).toBe('it is back at or below 1.25')
    expect(rearmAt('price', 'touch', 5400, 0.5, DEFAULTS)).toBe('it has moved 27 away from the level')
    expect(rearmAt('price', 'above', null, null, DEFAULTS)).toBeNull()
  })

  it('warns that a level already passed fires only after a real cross', () => {
    expect(sideNote('price', 'above', 24800, reading(25010))).toBe(
      'Now 25,010, already above 24,800: it fires only after the price has been at or below 24,800 and goes above again.',
    )
    expect(sideNote('pcr', 'below', 0.8, reading(0.72, { kind: 'pcr' }))).toContain('already below 0.80')
    expect(sideNote('price', 'above', 24800, reading(24750))).toBe('Now 24,750, 50 (0.20%) from 24,800.')
    expect(sideNote('price', 'above', 24800, reading(null, { state: 'no-quote' }))).toBeNull()
  })

  it('shows a number only with its age, and says why when there is none to compare', () => {
    const now = Date.parse('2026-10-30T04:30:04Z')
    expect(readingLine(reading(24812.35), now)).toEqual({ text: '24,812.35 · 4s ago', tone: 'pos' })
    expect(readingLine(reading(1.32, { kind: 'pcr', context: '03 Nov expiry' }), now).text).toBe('1.32 · 4s ago · 03 Nov expiry')
    expect(readingLine(reading(24790, { state: 'stale', detail: 'the last quote is 2 min old' }), now)).toEqual({
      text: '24,790 · the last quote is 2 min old',
      tone: 'warn',
    })
    expect(readingLine(reading(null, { state: 'no-quote', detail: 'no live quote: not streamed by any feed' }), now).tone).toBe('warn')
    expect(readingLine(null, now).tone).toBe('neutral')
  })

  it('formats a PCR as the chain page does and a price in Indian grouping', () => {
    expect(formatLevel('pcr', 1.3)).toBe('1.30')
    expect(formatLevel('price', 123450.5)).toBe('1,23,450.5')
    expect(formatLevel('price', null)).toBe('—')
  })

  it('describes a rule’s state and where each fire went', () => {
    expect(statusLine({ status: 'active', repeat: true, armed: false, fireCount: 2 })).toBe('fired 2× · waiting to re-arm')
    expect(statusLine({ status: 'active', repeat: false, armed: true, fireCount: 0 })).toBe('watching')
    expect(statusLine({ status: 'fired', repeat: false, armed: false, fireCount: 1 })).toBe('fired, done')
    expect(deliveryLine({ deliveredToTelegram: true, telegramNote: null })).toBe('Console and Telegram')
    expect(deliveryLine({ deliveredToTelegram: false, telegramNote: "Telegram carries an admin's alerts only." })).toBe(
      "Console · Telegram carries an admin's alerts only.",
    )
  })
})

describe('the top bar', () => {
  it('shows a count only for new alerts, and nothing while the answer is unknown', () => {
    expect(alertsBadge(undefined)).toBeNull()
    expect(alertsBadge({ unseen: 0, firedToday: 4, newestUtc: null, newestTitle: null })).toBeNull()
    expect(alertsBadge({ unseen: 2, firedToday: 2, newestUtc: '2026-10-30T04:31:00Z', newestTitle: 'NIFTY crossed above 24,800' })).toEqual({
      count: '2',
      title: '2 new level alerts. Newest: NIFTY crossed above 24,800',
    })
    expect(alertsBadge({ unseen: 140, firedToday: 140, newestUtc: null, newestTitle: null })?.count).toBe('99+')
  })
})
