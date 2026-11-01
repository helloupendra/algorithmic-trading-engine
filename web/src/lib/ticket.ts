/**
 * The manual order ticket's rules: which instrument it may show and order, when
 * Buy and Sell may be pressed, and the form that belongs to that instrument.
 *
 * Review of 2 Nov (F1): the instrument query kept the previous answer as
 * placeholder data across symbols, so after the box was cleared or retyped the
 * ticket still showed the old contract, with its price frozen (the poll had
 * stopped) and Buy and Sell enabled, and an order went to the OLD symbol. The
 * quantity, limit, stop and target typed for one instrument also stayed for the
 * next: 50 shares of SBIN became 50 lots of a NIFTY option. The chain and the
 * builder already guard the same trap ("keepPreviousData must never show one
 * underlying's chain under another's"); this is that guard for the one screen
 * that places orders, kept pure so it is tested.
 */

import { api } from './api'
import type { ManualInstrument } from './queries'

/** The shape of a broker symbol in the box, e.g. "NSE:SBIN-EQ": an exchange, a colon, then at least three characters. */
const SYMBOL_SHAPE = /^[A-Za-z]+:.{3,}$/

/**
 * The instrument query's options. Polled fast: the operator is reading a bid
 * and an ask they are about to hit. There is deliberately no placeholder: an
 * answer for one symbol is never shown, even for a moment, under another.
 * Between polls of the same symbol the query keeps its own data anyway.
 */
export function manualInstrumentQuery(
  symbol: string,
  fetchInstrument: (symbol: string) => Promise<ManualInstrument> = (s) =>
    api.get<ManualInstrument>(`/api/ManualOrders/instrument?symbol=${encodeURIComponent(s)}`),
) {
  const trimmed = symbol.trim()
  return {
    queryKey: ['manual', 'instrument', trimmed] as const,
    queryFn: () => fetchInstrument(trimmed),
    // Only once the box holds something shaped like a broker symbol. Firing on
    // every keystroke asked the API about "M", "MC", "MCX:C"... and each 404
    // painted "not in the instrument master" under a field still being typed in.
    enabled: SYMBOL_SHAPE.test(trimmed),
    refetchInterval: 2_000,
    retry: false,
  }
}

/** What the ticket reads off the instrument query (a subset of TanStack's result). */
export interface InstrumentReading {
  data: ManualInstrument | undefined
  isPlaceholderData: boolean
  isSuccess: boolean
  /** The query has answered since this symbol was put in the box (not merely held from an earlier visit). */
  isFetchedAfterMount: boolean
}

/** One symbol, however the box and the API spell its case and spaces. */
export function sameSymbol(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim().toUpperCase() === (b ?? '').trim().toUpperCase()
}

/**
 * The instrument the ticket may show: the answer for what is in the box now,
 * never one held over from a previous symbol.
 */
export function ticketInstrument(reading: InstrumentReading, symbol: string): ManualInstrument | undefined {
  const data = reading.data
  return data && !reading.isPlaceholderData && sameSymbol(data.symbol, symbol) ? data : undefined
}

/**
 * Whether the ticket may order the instrument it shows: its own answer has
 * arrived since it was picked (a cached one from an earlier visit is shown but
 * not traded on until it is refreshed), and the latest poll succeeded.
 */
export function ticketLive(reading: InstrumentReading, symbol: string): boolean {
  return ticketInstrument(reading, symbol) != null && reading.isSuccess && reading.isFetchedAfterMount
}

/** What the operator has typed into the ticket, and for which symbol. */
export interface TicketForm {
  symbol: string
  quantity: string
  orderType: 'market' | 'limit'
  limitPrice: string
  stopLoss: string
  target: string
  carryForward: boolean
}

/** A fresh form: one unit, market, no levels, intraday. */
export function blankTicket(symbol: string): TicketForm {
  return {
    symbol: symbol.trim(),
    quantity: '1',
    orderType: 'market',
    limitPrice: '',
    stopLoss: '',
    target: '',
    carryForward: false,
  }
}

/**
 * The form for the symbol in the box: what was typed for it, or a blank form
 * when what was typed belongs to another instrument. Units, prices and the
 * carry tick are never carried from one instrument to the next.
 */
export function ticketFormFor(form: TicketForm, symbol: string): TicketForm {
  return sameSymbol(form.symbol, symbol) ? form : blankTicket(symbol)
}

/** The ticket's checks on what is typed, against the instrument shown. */
export interface TicketCheck {
  qty: number
  qtyValid: boolean
  /** The limit sent with the order; null for a market order, or a limit box left empty. */
  limit: number | null
  limitMissing: boolean
  limitValid: boolean
  offTick: boolean
  /** Buy and Sell may be pressed, as far as the form and the instrument go (each side still needs its price). */
  ready: boolean
}

export function checkTicket(form: TicketForm, instrument: ManualInstrument | undefined, live: boolean): TicketCheck {
  const qty = Number.parseInt(form.quantity, 10)
  // Only a Limit ticket sends a price. A number left behind in the box after
  // switching back to Market must not quietly become the order's price.
  const limit = form.orderType === 'limit' && form.limitPrice.trim() !== '' ? Number(form.limitPrice) : null
  const limitMissing = form.orderType === 'limit' && form.limitPrice.trim() === ''
  const qtyValid = Number.isFinite(qty) && qty >= 1
  const limitValid = limit == null || (Number.isFinite(limit) && limit > 0)
  // The tick rule is enforced by the API too; checking here turns a rejected
  // round-trip into a message under the field the operator is still typing in.
  const tick = instrument?.tickSize
  const offTick = limit != null && tick ? Math.abs(limit / tick - Math.round(limit / tick)) > 0.0001 : false
  const ready =
    instrument != null && live && !instrument.expired && qtyValid && limitValid && !offTick && !limitMissing
  return { qty, qtyValid, limit, limitMissing, limitValid, offTick, ready }
}

/**
 * Whether one side can be pressed: the form is ready and the API says what that
 * side would fill at now. With no such price (no quote, or one too old while
 * the market is open) the API would refuse it, a limit included.
 */
export function sideEnabled(check: TicketCheck, instrument: ManualInstrument | undefined, side: 'BUY' | 'SELL'): boolean {
  if (!check.ready || !instrument) return false
  return (side === 'BUY' ? instrument.buyAt : instrument.sellAt) != null
}

/**
 * Whether a limit is reached by the book now: a buy at or above the price it
 * would fill at (the ask), a sell at or below it (the bid). Null when there is
 * no limit or no price to compare with. The API decides; this only warns.
 */
export function limitReached(limit: number | null, instrument: ManualInstrument | undefined, side: 'BUY' | 'SELL'): boolean | null {
  if (limit == null || !instrument) return null
  const at = side === 'BUY' ? instrument.buyAt : instrument.sellAt
  if (at == null) return null
  return side === 'BUY' ? at <= limit : at >= limit
}

/** The button's words: "Buy at 150.5" for a market order, "Buy, limit 155" for a limit. */
export function sideLabel(side: 'BUY' | 'SELL', limit: number | null, instrument: ManualInstrument): string {
  const verb = side === 'BUY' ? 'Buy' : 'Sell'
  if (limit != null) return `${verb}, limit ${limit}`
  return `${verb} at ${(side === 'BUY' ? instrument.buyAt : instrument.sellAt) ?? '—'}`
}

/**
 * What a limit means here, and which side the book has not reached: the API
 * fills a limit at the book only once the book is at it or better, and refuses
 * it otherwise. There are no resting orders. The ticket does not know which
 * side is meant, so only a limit neither side would fill is a warning.
 */
export function limitHint(limit: number | null, instrument: ManualInstrument | undefined): { text: string; warn: boolean } {
  const rule = 'Fills at the book only once the book has reached it (a buy at the ask, a sell at the bid); no resting orders.'
  if (limit == null || !instrument) return { text: rule, warn: false }
  const buy = limitReached(limit, instrument, 'BUY')
  const sell = limitReached(limit, instrument, 'SELL')
  if (buy === false && sell === false) {
    return { text: `${rule} Neither side fills now: a buy is at ${instrument.buyAt}, a sell at ${instrument.sellAt}.`, warn: true }
  }
  if (buy === false) return { text: `${rule} A buy would not fill now: it is at ${instrument.buyAt}.`, warn: false }
  if (sell === false) return { text: `${rule} A sell would not fill now: it is at ${instrument.sellAt}.`, warn: false }
  return { text: rule, warn: false }
}

/**
 * The confirm dialog's question. It names the contract, the quantity in its own
 * unit with what it comes to ("2 lots (130 qty)"), the price it would fill at
 * now, the limit when there is one, and the product.
 */
export function confirmText(side: 'BUY' | 'SELL', check: TicketCheck, instrument: ManualInstrument, carryForward: boolean): string {
  const now = side === 'BUY' ? instrument.buyAt : instrument.sellAt
  const at =
    check.limit != null
      ? `with a limit of ${check.limit} (fills at the ${side === 'BUY' ? 'ask, at most' : 'bid, at least'} ${check.limit}; ${now ?? '—'} now)`
      : `at ${now ?? '—'} (the price it fills at now)`
  const product = carryForward ? 'carry forward (held overnight)' : 'intraday'
  return `${side} ${quantityWords(check.qty, instrument)} of ${instrument.symbol} ${at}, ${product}?`
}

/** "2 lots (130 qty)", "50 shares": the quantity in the instrument's own unit, with what it comes to. */
export function quantityWords(qty: number, instrument: Pick<ManualInstrument, 'quantityUnit' | 'lotSize'>): string {
  const lots = instrument.quantityUnit === 'lots'
  const word = lots ? (qty === 1 ? 'lot' : 'lots') : qty === 1 ? 'share' : 'shares'
  return lots ? `${qty} ${word} (${qty * instrument.lotSize} qty)` : `${qty} ${word}`
}
