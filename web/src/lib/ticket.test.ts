import { QueryClient, QueryObserver } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { ManualInstrument } from './queries'
import {
  blankTicket,
  checkTicket,
  confirmText,
  limitHint,
  manualInstrumentQuery,
  quantityWords,
  sameSymbol,
  sideEnabled,
  sideLabel,
  ticketFormFor,
  ticketInstrument,
  ticketLive,
} from './ticket'
import type { InstrumentReading, TicketForm } from './ticket'

const NIFTY_CE = 'NSE:NIFTY25N0425000CE'
const SBIN = 'NSE:SBIN-EQ'
const CRUDE = 'MCX:CRUDEOIL25NOVFUT'

function instrument(symbol: string, over: Partial<ManualInstrument> = {}): ManualInstrument {
  const derivative = symbol !== SBIN
  return {
    symbol,
    exchange: symbol.split(':')[0],
    segment: derivative ? 'FO' : 'CM',
    segmentLabel: derivative ? 'NSE F&O option' : 'NSE equity',
    instrumentType: derivative ? 'CE' : 'EQ',
    underlying: null,
    strikePrice: null,
    expiryDate: null,
    tickSize: 0.05,
    lotSize: derivative ? 65 : 1,
    lotSizeSource: 'test',
    quantityUnit: derivative ? 'lots' : 'shares',
    ltp: 101.5,
    bid: 101,
    ask: 101.5,
    bidSize: 650,
    askSize: 1300,
    open: null,
    high: null,
    low: null,
    prevClose: null,
    volume: null,
    change: null,
    changePercent: null,
    quoteUpdatedUtc: '2026-11-02T04:00:00Z',
    subscribing: false,
    expired: false,
    buyAt: 101.5,
    sellAt: 101,
    tradable: true,
    ...over,
  }
}

/** Answers each symbol with its instrument; a symbol in `held` waits until released. */
function fakeApi() {
  const calls: string[] = []
  const held = new Map<string, () => void>()
  const hold = new Set<string>()
  const fetcher = (symbol: string) => {
    calls.push(symbol)
    if (!hold.has(symbol)) return Promise.resolve(instrument(symbol))
    return new Promise<ManualInstrument>((resolve) => held.set(symbol, () => resolve(instrument(symbol))))
  }
  return { calls, fetcher, hold: (s: string) => hold.add(s), release: (s: string) => held.get(s)?.() }
}

const clients: QueryClient[] = []
const observers: Array<() => void> = []
afterEach(() => {
  observers.splice(0).forEach((stop) => stop())
  clients.splice(0).forEach((qc) => qc.clear())
})

/** The ticket's query exactly as useManualInstrument builds it, with the API answered by `fetcher`. */
function ticketQuery(symbol: string, fetcher: (symbol: string) => Promise<ManualInstrument>) {
  const qc = new QueryClient()
  clients.push(qc)
  const observer = new QueryObserver(qc, manualInstrumentQuery(symbol, fetcher))
  observers.push(observer.subscribe(() => {}))
  return {
    read: (): InstrumentReading => observer.getCurrentResult(),
    type: (next: string) => observer.setOptions(manualInstrumentQuery(next, fetcher)),
    settled: () => vi.waitFor(() => expect(observer.getCurrentResult().isFetching).toBe(false)),
  }
}

describe('the ticket never shows or orders an instrument the box no longer names (review 2 Nov, F1)', () => {
  it('drops the old contract the moment the box is cleared or retyped', async () => {
    // The reviewer's proof, turned round: before the fix `data` stayed the
    // NIFTY CE (as placeholder, poll stopped) under an empty box and under a
    // half-typed search, and Buy sent the old symbol.
    const api = fakeApi()
    const q = ticketQuery(NIFTY_CE, api.fetcher)
    await q.settled()
    expect(ticketInstrument(q.read(), NIFTY_CE)?.symbol).toBe(NIFTY_CE)
    expect(ticketLive(q.read(), NIFTY_CE)).toBe(true)

    q.type('') // box cleared
    expect(q.read().data).toBeUndefined()
    expect(q.read().isPlaceholderData).toBe(false)
    expect(ticketInstrument(q.read(), '')).toBeUndefined()
    expect(ticketLive(q.read(), '')).toBe(false)

    q.type('BANKNIFTY') // half-typed search: the query is disabled
    expect(q.read().data).toBeUndefined()
    expect(ticketInstrument(q.read(), 'BANKNIFTY')).toBeUndefined()
  })

  it('shows nothing of the previous contract while a newly picked one loads, and orders nothing until it has', async () => {
    const api = fakeApi()
    const q = ticketQuery(SBIN, api.fetcher)
    await q.settled()

    api.hold(CRUDE)
    q.type(CRUDE)
    expect(q.read().data).toBeUndefined()
    expect(ticketInstrument(q.read(), CRUDE)).toBeUndefined()
    expect(ticketLive(q.read(), CRUDE)).toBe(false)

    api.release(CRUDE)
    await q.settled()
    expect(ticketInstrument(q.read(), CRUDE)?.symbol).toBe(CRUDE)
    expect(ticketLive(q.read(), CRUDE)).toBe(true)
  })

  it('shows a contract held from an earlier visit but trades it only once it has been asked again', async () => {
    const api = fakeApi()
    const q = ticketQuery(SBIN, api.fetcher)
    await q.settled()
    q.type(CRUDE)
    await q.settled()

    api.hold(SBIN)
    q.type(SBIN) // back to SBIN: the cache still holds its earlier answer
    expect(ticketInstrument(q.read(), SBIN)?.symbol).toBe(SBIN)
    expect(ticketLive(q.read(), SBIN)).toBe(false)

    api.release(SBIN)
    await q.settled()
    expect(ticketLive(q.read(), SBIN)).toBe(true)
    expect(api.calls).toEqual([SBIN, CRUDE, SBIN])
  })

  it('does not ask the API about a box that is not yet a broker symbol', () => {
    expect(manualInstrumentQuery('').enabled).toBe(false)
    expect(manualInstrumentQuery('BANKNIFTY').enabled).toBe(false)
    expect(manualInstrumentQuery('MCX:C').enabled).toBe(false)
    expect(manualInstrumentQuery(` ${SBIN} `).enabled).toBe(true)
    expect(manualInstrumentQuery(` ${SBIN} `).queryKey).toEqual(['manual', 'instrument', SBIN])
    expect('placeholderData' in manualInstrumentQuery(SBIN)).toBe(false)
  })

  it('refuses an answer for another symbol even when it is not marked as a placeholder', () => {
    const reading = (data: ManualInstrument | undefined, over: Partial<InstrumentReading> = {}): InstrumentReading => ({
      data,
      isPlaceholderData: false,
      isSuccess: true,
      isFetchedAfterMount: true,
      ...over,
    })
    expect(ticketInstrument(reading(instrument(NIFTY_CE)), CRUDE)).toBeUndefined()
    expect(ticketInstrument(reading(instrument(NIFTY_CE), { isPlaceholderData: true }), NIFTY_CE)).toBeUndefined()
    expect(ticketInstrument(reading(instrument(SBIN)), ' nse:sbin-eq ')?.symbol).toBe(SBIN)
    // The last poll failed: the instrument stays on screen, but is not traded on.
    expect(ticketLive(reading(instrument(SBIN), { isSuccess: false }), SBIN)).toBe(false)
  })
})

describe('the form belongs to one instrument', () => {
  const typedForSbin: TicketForm = {
    symbol: SBIN,
    quantity: '50',
    orderType: 'limit',
    limitPrice: '812.5',
    stopLoss: '800',
    target: '830',
    carryForward: true,
  }

  it('is blank for the next instrument: 50 shares never become 50 lots', () => {
    expect(ticketFormFor(typedForSbin, NIFTY_CE)).toEqual(blankTicket(NIFTY_CE))
    expect(blankTicket(NIFTY_CE)).toEqual({
      symbol: NIFTY_CE,
      quantity: '1',
      orderType: 'market',
      limitPrice: '',
      stopLoss: '',
      target: '',
      carryForward: false,
    })
    expect(ticketFormFor(typedForSbin, '')).toEqual(blankTicket(''))
  })

  it('keeps what was typed while the box still names the same instrument', () => {
    expect(ticketFormFor(typedForSbin, SBIN)).toBe(typedForSbin)
    expect(ticketFormFor(typedForSbin, ' nse:sbin-eq')).toBe(typedForSbin)
    expect(sameSymbol(SBIN, NIFTY_CE)).toBe(false)
  })
})

describe('Buy and Sell', () => {
  const nifty = instrument(NIFTY_CE)

  it('stay disabled until the instrument is live, and for a side with no price', () => {
    const form = blankTicket(NIFTY_CE)
    expect(checkTicket(form, undefined, false).ready).toBe(false)
    expect(checkTicket(form, nifty, false).ready).toBe(false)
    const ready = checkTicket(form, nifty, true)
    expect(ready.ready).toBe(true)
    expect(sideEnabled(ready, nifty, 'BUY')).toBe(true)

    // A quote too old while the market is open: the API gives no price, so a
    // market order and a limit alike would be refused.
    const stale = instrument(NIFTY_CE, { buyAt: null, sellAt: null, tradable: false })
    const limitForm = { ...form, orderType: 'limit' as const, limitPrice: '105' }
    expect(sideEnabled(checkTicket(limitForm, stale, true), stale, 'BUY')).toBe(false)
    expect(sideEnabled(checkTicket(form, stale, true), stale, 'SELL')).toBe(false)

    expect(checkTicket(form, instrument(NIFTY_CE, { expired: true }), true).ready).toBe(false)
  })

  it('check the form: quantity, a missing or off-tick limit, and a market order sends no price', () => {
    const form = blankTicket(NIFTY_CE)
    expect(checkTicket({ ...form, quantity: '0' }, nifty, true).ready).toBe(false)
    expect(checkTicket({ ...form, orderType: 'limit' }, nifty, true)).toMatchObject({ limitMissing: true, ready: false })
    expect(checkTicket({ ...form, orderType: 'limit', limitPrice: '101.52' }, nifty, true)).toMatchObject({ offTick: true, ready: false })
    expect(checkTicket({ ...form, orderType: 'market', limitPrice: '0.05' }, nifty, true).limit).toBeNull()
  })

  it('say what is ordered, in the unit and with the quantity it comes to', () => {
    const check = checkTicket({ ...blankTicket(NIFTY_CE), quantity: '2' }, nifty, true)
    expect(confirmText('BUY', check, nifty, false)).toBe(
      `BUY 2 lots (130 qty) of ${NIFTY_CE} at 101.5 (the price it fills at now), intraday?`,
    )
    const limit = checkTicket({ ...blankTicket(NIFTY_CE), quantity: '2', orderType: 'limit', limitPrice: '105' }, nifty, true)
    expect(confirmText('BUY', limit, nifty, true)).toBe(
      `BUY 2 lots (130 qty) of ${NIFTY_CE} with a limit of 105 (fills at the ask, at most 105; 101.5 now), carry forward (held overnight)?`,
    )
    expect(quantityWords(50, instrument(SBIN))).toBe('50 shares')
    expect(sideLabel('BUY', null, nifty)).toBe('Buy at 101.5')
    expect(sideLabel('SELL', 99, nifty)).toBe('Sell, limit 99')
  })

  it('say which side a limit would not fill, and warn when neither would', () => {
    expect(limitHint(0.05, nifty).text).toContain('A buy would not fill now: it is at 101.5.')
    expect(limitHint(101.5, nifty).text).toContain('A sell would not fill now: it is at 101.')
    expect(limitHint(101.25, nifty)).toMatchObject({ warn: true })
    expect(limitHint(101.25, nifty).text).toContain('Neither side fills now: a buy is at 101.5, a sell at 101.')
    expect(limitHint(101.5, instrument(NIFTY_CE, { sellAt: 101.5 })).text).not.toContain('would not fill')
    expect(limitHint(null, nifty).warn).toBe(false)
  })
})
