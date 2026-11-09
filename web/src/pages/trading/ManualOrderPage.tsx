/**
 * Trading module — the manual order ticket.
 *
 * Everything else the platform books comes from a strategy. This is the ticket
 * for an order the operator decides on, in any segment: an MCX future, an NSE
 * share, an option on either.
 *
 * Two things it refuses to guess, because they differ per instrument and
 * getting them wrong is how a paper book stops resembling a real one:
 *   - the quantity UNIT: lots for a derivative, shares for equity;
 *   - the tick size: 1.00 on an MCX crude future, 0.10 on its options, 0.05 on
 *     a BANKNIFTY option, 0.10 on a share.
 * Both come from the instrument master through the API, so the form is told
 * what to enforce rather than assuming one segment's rules everywhere.
 *
 * Prices are the live book, not the last trade: Buy shows the ask it would pay
 * and Sell the bid it would hit. The book below is the same RunCard every
 * strategy run uses, so positions, P&L and per-position square-off are the
 * ones already understood elsewhere.
 *
 * Each order is intraday unless "Carry forward" is ticked (27 Sep, the owner:
 * "if I want to carry forward, there should be a tick there and ticking it is
 * enough"): intraday is squared off at its exchange's close, a carried
 * position is held overnight. The tick can be changed on the position in the
 * book afterwards.
 */

import { useState } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { accessFor, allows } from '../../lib/modules'
import { ApiError } from '../../lib/api'
import { beginOrder, finishOrder, pendingOrder, type ManualOrderIntent } from '../../lib/manualOrderIntent'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { Badge, EmptyState, FlashPrice, InlineError, Loading, Panel } from '../../components/ui'
import { RunCard } from '../strategies/RunCard'
import { formatAge, formatDateTime, formatNumber, formatPrice } from '../../lib/format'
import { ticketCarryHint } from '../../lib/carry'
import { useManualBook, useManualInstrument, usePlaceManualOrder, useStrategyLive } from '../../lib/queries'
import type { ManualInstrument } from '../../lib/queries'
import {
  blankTicket,
  checkTicket,
  confirmText,
  limitHint,
  sameSymbol,
  sideEnabled,
  sideLabel,
  ticketFormFor,
  ticketInstrument,
  ticketLive,
} from '../../lib/ticket'
import type { TicketForm } from '../../lib/ticket'
import './ticket.css'

/** One side of the book, with the size resting there. */
function BookSide({
  label,
  price,
  size,
  tone,
}: {
  label: string
  price: number | null
  size: number | null
  tone: 'pos' | 'neg'
}) {
  return (
    <div className="stat">
      <div className={`stat__value ${price == null ? 'muted' : tone}`}>
        {price == null ? '—' : formatPrice(price)}
      </div>
      <div className="stat__label">{label}</div>
      <div className="stat__sub">{size == null ? 'no size' : `${formatNumber(size)} qty`}</div>
    </div>
  )
}

/**
 * The live price, given the room it deserves.
 *
 * The bid and the ask are what an order actually pays, but the last trade and
 * its move are what tells the operator whether the instrument is going
 * anywhere - and a number that never visibly changes reads as a dead page, so
 * the price flashes on every tick and says how old it is.
 */
function LivePrice({ instrument }: { instrument: ManualInstrument }) {
  const up = (instrument.change ?? 0) > 0
  const down = (instrument.change ?? 0) < 0
  const tone = up ? 'pos' : down ? 'neg' : 'muted'

  return (
    <div className="manual-price">
      <div className="manual-price__main">
        <span className="manual-price__ltp mono">
          <FlashPrice value={instrument.ltp} bold />
        </span>
        {instrument.change != null && (
          <span className={`manual-price__change ${tone}`}>
            {up ? '▲' : down ? '▼' : ''} {formatPrice(Math.abs(instrument.change))}
            {instrument.changePercent != null && ` (${instrument.changePercent.toFixed(2)}%)`}
          </span>
        )}
      </div>
      <div className="manual-price__meta muted">
        <span>O {instrument.open == null ? '—' : formatPrice(instrument.open)}</span>
        <span>H {instrument.high == null ? '—' : formatPrice(instrument.high)}</span>
        <span>L {instrument.low == null ? '—' : formatPrice(instrument.low)}</span>
        <span>Prev {instrument.prevClose == null ? '—' : formatPrice(instrument.prevClose)}</span>
        {instrument.volume != null && instrument.volume > 0 && (
          <span>Vol {formatNumber(instrument.volume)}</span>
        )}
        {instrument.quoteUpdatedUtc && (
          <span title={formatDateTime(instrument.quoteUpdatedUtc)}>
            · {formatAge(instrument.quoteUpdatedUtc)}
          </span>
        )}
      </div>
    </div>
  )
}

function InstrumentRules({ instrument }: { instrument: ManualInstrument }) {
  return (
    <div className="deploy-card__meta muted" style={{ flexWrap: 'wrap' }}>
      <Badge tone="accent">{instrument.segmentLabel}</Badge>
      <span>
        · tick <span className="mono">{instrument.tickSize ?? '—'}</span>
      </span>
      <span>
        · lot size <span className="mono">{formatNumber(instrument.lotSize)}</span>
        {instrument.lotSizeSource ? ` (${instrument.lotSizeSource})` : ''}
      </span>
      <span>
        · ordered in <strong>{instrument.quantityUnit}</strong>
      </span>
      {instrument.expiryDate && <span>· expires {instrument.expiryDate}</span>}
    </div>
  )
}

export function ManualOrderPage() {
  const { user } = useAuth()
  if (!allows(accessFor(user), 'strategies')) return <Navigate to="/forbidden" replace />
  return <ManualOrderTicket />
}

function ManualOrderTicket() {
  const { user } = useAuth()
  const [savedIntent] = useState(() => {
    try { return { pending: user ? pendingOrder(sessionStorage, user.id) : null, error: null as unknown } }
    catch (error) { return { pending: null, error } }
  })
  const [pending, setPending] = useState(savedIntent.pending)
  const [intentError, setIntentError] = useState<unknown>(savedIntent.error)
  const [symbol, setSymbol] = useState('')
  // What is typed belongs to one instrument and is cleared when the box names
  // another (lib/ticket.ts): 50 shares of SBIN must never become 50 lots of a
  // NIFTY option, nor a limit or stop typed for one contract be sent for the
  // next. Intraday (carry unticked) unless the owner says otherwise.
  const [storedForm, setForm] = useState<TicketForm>(() => blankTicket(''))
  const form = ticketFormFor(storedForm, symbol)
  const edit = (patch: Partial<TicketForm>) => setForm((prev) => ({ ...ticketFormFor(prev, symbol), ...patch }))
  const changeSymbol = (next: string) => {
    if (!sameSymbol(next, symbol)) setForm(blankTicket(next))
    setSymbol(next)
  }

  const instrumentQuery = useManualInstrument(symbol)
  const book = useManualBook()
  const bookRunId = book.data?.runId ?? null
  // The book's card paints its header from the run's live view, and without
  // one it falls back to "Stopped · started —": a known-nothing rendered as a
  // fact for the seconds before the view arrives, and for ever when the call
  // fails. Asking for the view here as well (same cache key, one request) lets
  // the panel say "opening" or show the failure until the view is known.
  const bookLive = useStrategyLive(bookRunId ?? 0, bookRunId != null)
  const place = usePlaceManualOrder()

  // Only the answer for what is in the box now: never the previous symbol's,
  // held over while the next one loads or after the box is cleared. Buy and
  // Sell wait for this instrument's own answer (not a cached one from an
  // earlier visit) and stop when its poll fails.
  const instrument = ticketInstrument(instrumentQuery, symbol)
  const live = ticketLive(instrumentQuery, symbol)
  const check = checkTicket(form, instrument, live)
  const { qty, qtyValid, limit, limitMissing, offTick } = check
  const canBuy = instrument?.supportsIdempotency === true && !intentError && !pending && !place.isPending && sideEnabled(check, instrument, 'BUY')
  const canSell = instrument?.supportsIdempotency === true && !intentError && !pending && !place.isPending && sideEnabled(check, instrument, 'SELL')

  function submit(side: 'BUY' | 'SELL') {
    if (!instrument || !(side === 'BUY' ? canBuy : canSell)) return
    if (!window.confirm(confirmText(side, check, instrument, form.carryForward))) return

    if (!user) return
    try {
      const intent = beginOrder(sessionStorage, user.id, {
        symbol: instrument.symbol, side, quantity: qty, limitPrice: limit,
        stopLossPrice: form.stopLoss.trim() === '' ? null : Number(form.stopLoss),
        targetPrice: form.target.trim() === '' ? null : Number(form.target), carryForward: form.carryForward,
      })
      send(intent)
    } catch (error) { setIntentError(error) }
  }

  function send(intent: ManualOrderIntent) {
    if (!user) return
    setPending(intent)
    setIntentError(null)
    const finish = () => {
      try {
        finishOrder(sessionStorage, user.id, intent.clientOrderId)
        setPending(null)
      } catch (error) { setIntentError(error) }
    }
    place.mutate(intent, {
      onSuccess: finish,
      onError: error => {
        // An ambiguous transport/server error keeps the original key and payload.
        if (error instanceof ApiError && error.status === 400) finish()
      },
    })
  }

  const unitWord = instrument?.quantityUnit ?? 'lots'
  const units = instrument && qtyValid ? qty * instrument.lotSize : null
  const hint = limitHint(limit, instrument)

  return (
    <div className="page ticket-page">
      {intentError != null && <InlineError error={intentError} />}
      {pending && (
        <Panel title="Order outcome pending">
          <p>{pending.side} {pending.quantity} of {pending.symbol}. Recover this order before placing another.</p>
          <p className="small-note muted">If it already filled, recovery returns the original result. It cannot place it twice.</p>
          <button className="btn" disabled={place.isPending} onClick={() => send(pending)}>
            {place.isPending ? 'Checking order…' : 'Recover order outcome'}
          </button>
        </Panel>
      )}
      <header className="page__header">
        <div>
          <h1 className="page__title">Manual order</h1>
          <p className="page__subtitle">
            Place a paper order by hand in any segment. Quantity and tick size follow the instrument's own
            rules, and a buy fills at the ask while a sell hits the bid — not the last trade, which can be
            stale and on the wrong side of the spread.
          </p>
        </div>
      </header>

      <Panel title="Ticket">
        {/* The example shows the search's grammar, not a contract: a dated
            symbol in a placeholder expires with the contract and was wrong
            eleven months of the year on a ticket that refuses expired ones. */}
        <div className="field ticket__symbol">
          <label className="field__label" htmlFor="manual-symbol">
            Instrument
          </label>
          <SymbolCombobox
            id="manual-symbol"
            value={symbol}
            onChange={changeSymbol}
            placeholder="SBIN, CRUDEOIL, NIFTY 25000 CE…"
          />
          <p className="field__help">
            Commodity, equity or option — a name, a strike with CE or PE, or the exact symbol
            (NSE:SBIN-EQ). Expired contracts are hidden.
          </p>
        </div>

        {instrumentQuery.isError && <InlineError error={instrumentQuery.error} />}

        {symbol.trim() !== '' && instrumentQuery.isLoading && <Loading label="Reading the book…" />}

        {instrument && (
          <>
            <InstrumentRules instrument={instrument} />
            {!instrument.supportsIdempotency && <p role="status">The order service is updating. Refresh this page before placing an order.</p>}
            <LivePrice instrument={instrument} />

            <div
              className="stat-grid"
              style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', marginTop: 12 }}
            >
              <BookSide label="Bid (sell here)" price={instrument.bid} size={instrument.bidSize} tone="neg" />
              <BookSide label="Ask (buy here)" price={instrument.ask} size={instrument.askSize} tone="pos" />
              <div className="stat">
                <div className="stat__value mono">{instrument.ltp == null ? '—' : formatPrice(instrument.ltp)}</div>
                <div className="stat__label">Last trade</div>
                <div className="stat__sub">
                  {instrument.bid != null && instrument.ask != null
                    ? `spread ${formatPrice(instrument.ask - instrument.bid)}`
                    : 'no two-sided market'}
                </div>
              </div>
            </div>

            {instrument.expired ? (
              <p className="neg" style={{ marginTop: 10 }}>
                This contract expired on {instrument.expiryDate} — it will never quote again and cannot be
                traded. Pick a live expiry.
              </p>
            ) : (
              !instrument.tradable && (
                <p className="muted" style={{ marginTop: 10 }}>
                  {instrument.untradableReason
                    ? `Not tradable now: ${instrument.untradableReason}. Orders open again with the next fresh tick.`
                    : `${
                        instrument.subscribing
                          ? 'Just added to the live feed — the first tick usually lands within a few seconds. '
                          : 'No live price yet. '
                      }The market may also be closed for this segment. A limit order needs a live price too.`}
                </p>
              )
            )}

            <div className="form-row" style={{ marginTop: 12 }}>
              <div className="field">
                <label className="field__label" htmlFor="manual-qty">
                  Quantity ({unitWord})
                </label>
                <input
                  id="manual-qty"
                  className="field__input"
                  type="number"
                  min={1}
                  step={1}
                  value={form.quantity}
                  onChange={(e) => edit({ quantity: e.target.value })}
                />
                <p className="field__help">
                  {units == null
                    ? ' '
                    : `${formatNumber(qty)} ${unitWord} × ${formatNumber(instrument.lotSize)} = ${formatNumber(units)} qty`}
                </p>
              </div>

              <div className="field">
                <label className="field__label" htmlFor="manual-limit">
                  Price
                </label>
                {/* Market was reachable before only by leaving the box empty,
                    which is a rule you have to be told. Saying it outright also
                    removes the ambiguity of a half-typed price sitting in a
                    field the operator meant to ignore. */}
                <div className="seg" role="group" aria-label="Order type">
                  <button
                    type="button"
                    className={`seg__btn ${form.orderType === 'market' ? 'is-active' : ''}`}
                    aria-pressed={form.orderType === 'market'}
                    onClick={() => edit({ orderType: 'market' })}
                  >
                    Market
                  </button>
                  <button
                    type="button"
                    className={`seg__btn ${form.orderType === 'limit' ? 'is-active' : ''}`}
                    aria-pressed={form.orderType === 'limit'}
                    onClick={() => edit({ orderType: 'limit' })}
                  >
                    Limit
                  </button>
                </div>

                {form.orderType === 'limit' ? (
                  <>
                    <input
                      id="manual-limit"
                      className="field__input"
                      style={{ marginTop: 8 }}
                      type="number"
                      step={instrument.tickSize ?? 0.05}
                      value={form.limitPrice}
                      onChange={(e) => edit({ limitPrice: e.target.value })}
                      placeholder={String(instrument.buyAt ?? instrument.ltp ?? '')}
                    />
                    <p className={`field__help ${offTick || limitMissing || hint.warn ? 'neg' : ''}`}>
                      {limitMissing
                        ? 'Enter a limit price, or switch to Market.'
                        : offTick
                          ? `Not a multiple of the ${instrument.tickSize} tick.`
                          : `Must be a multiple of ${instrument.tickSize ?? '—'}. ${hint.text}`}
                    </p>
                  </>
                ) : (
                  <p className="field__help">
                    Takes the book: a buy fills at {instrument.buyAt ?? '—'}, a sell at {instrument.sellAt ?? '—'}.
                  </p>
                )}
              </div>
            </div>

            <div className="form-row" style={{ marginTop: 12 }}>
              <div className="field">
                <label className="field__label" htmlFor="manual-sl">
                  Stop-loss price <span className="muted">(optional)</span>
                </label>
                <input
                  id="manual-sl"
                  className="field__input"
                  type="number"
                  step={instrument.tickSize ?? 0.05}
                  value={form.stopLoss}
                  onChange={(e) => edit({ stopLoss: e.target.value })}
                  placeholder="this position only"
                />
                <p className="field__help">Below the fill for a buy, above it for a sell.</p>
              </div>

              <div className="field">
                <label className="field__label" htmlFor="manual-target">
                  Target price <span className="muted">(optional)</span>
                </label>
                <input
                  id="manual-target"
                  className="field__input"
                  type="number"
                  step={instrument.tickSize ?? 0.05}
                  value={form.target}
                  onChange={(e) => edit({ target: e.target.value })}
                  placeholder="this position only"
                />
                <p className="field__help">Above the fill for a buy, below it for a sell.</p>
              </div>
            </div>

            <label className="manual-carry" htmlFor="manual-carry">
              <input
                id="manual-carry"
                type="checkbox"
                checked={form.carryForward}
                onChange={(e) => edit({ carryForward: e.target.checked })}
              />
              <span>
                <span className="manual-carry__label">Carry forward (hold overnight)</span>
                <span className="field__help">{ticketCarryHint(form.carryForward, instrument.symbol)}</span>
              </span>
            </label>

            <div className="row" style={{ gap: 10, marginTop: 10, flexWrap: 'wrap' }}>
              <button
                type="button"
                className="btn btn--pos"
                disabled={!canBuy}
                onClick={() => submit('BUY')}
              >
                {place.isPending ? 'Placing…' : sideLabel('BUY', limit, instrument)}
              </button>
              <button
                type="button"
                className="btn btn--danger"
                disabled={!canSell}
                onClick={() => submit('SELL')}
              >
                {place.isPending ? 'Placing…' : sideLabel('SELL', limit, instrument)}
              </button>
            </div>

            {place.isError && <InlineError error={place.error} />}
            {place.isSuccess && (
              <p className="pos" style={{ marginTop: 10 }}>
                {place.data.message}
              </p>
            )}
          </>
        )}

        {symbol.trim() === '' && <EmptyState>Pick an instrument to see its book and place an order.</EmptyState>}
      </Panel>

      <Panel title="Manual book">
        {book.isLoading || (bookRunId != null && bookLive.isPending) ? (
          <Loading label="Opening your book…" />
        ) : book.isError && book.data === undefined ? (
          <InlineError error={book.error} />
        ) : bookRunId != null && bookLive.isError && bookLive.data === undefined ? (
          <InlineError error={bookLive.error} />
        ) : bookRunId != null ? (
          <RunCard
            strategy={{ name: 'Manual', category: 'Manual' }}
            runId={bookRunId}
            run={null}
            exit={null}
            allowStop={false}
          />
        ) : (
          <EmptyState>
            Nothing traded by hand yet. Your first order opens a book, and it keeps your manual positions
            separate from every strategy's P&L.
          </EmptyState>
        )}
      </Panel>
    </div>
  )
}
