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
 */

import { useState } from 'react'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { Badge, EmptyState, FlashPrice, InlineError, Loading, Panel } from '../../components/ui'
import { RunCard } from '../strategies/RunCard'
import { formatAge, formatDateTime, formatNumber, formatPrice } from '../../lib/format'
import { useManualBook, useManualInstrument, usePlaceManualOrder } from '../../lib/queries'
import type { ManualInstrument } from '../../lib/queries'

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
  const [symbol, setSymbol] = useState('')
  const [quantity, setQuantity] = useState('1')
  const [limitPrice, setLimitPrice] = useState('')
  const [orderType, setOrderType] = useState<'market' | 'limit'>('market')
  const [stopLoss, setStopLoss] = useState('')
  const [target, setTarget] = useState('')

  const instrumentQuery = useManualInstrument(symbol)
  const book = useManualBook()
  const place = usePlaceManualOrder()

  const instrument = instrumentQuery.data
  const qty = Number.parseInt(quantity, 10)
  // Only a Limit ticket sends a price. A number left behind in the box after
  // switching back to Market must not quietly become the order's price.
  const limit = orderType === 'limit' && limitPrice.trim() !== '' ? Number(limitPrice) : null
  const limitMissing = orderType === 'limit' && limitPrice.trim() === ''

  const qtyValid = Number.isFinite(qty) && qty >= 1
  const limitValid = limit == null || (Number.isFinite(limit) && limit > 0)

  // The tick rule is enforced by the API too; checking here turns a rejected
  // round-trip into a message under the field the operator is still typing in.
  const offTick =
    limit != null && instrument?.tickSize
      ? Math.abs(limit / instrument.tickSize - Math.round(limit / instrument.tickSize)) > 0.0001
      : false

  const canPlace =
    !!instrument && !instrument.expired && qtyValid && limitValid && !offTick && !limitMissing && !place.isPending

  function submit(side: 'BUY' | 'SELL') {
    if (!instrument || !canPlace) return
    const price = side === 'BUY' ? instrument.buyAt : instrument.sellAt
    const what = `${side} ${qty} ${instrument.quantityUnit === 'lots' ? (qty === 1 ? 'lot' : 'lots') : qty === 1 ? 'share' : 'shares'}`
    const at = limit != null ? `at your limit ${limit}` : `at the ${side === 'BUY' ? 'ask' : 'bid'} ${price ?? '—'}`
    if (!window.confirm(`${what} of ${instrument.symbol} ${at}?`)) return

    place.mutate({
      symbol: instrument.symbol,
      side,
      quantity: qty,
      limitPrice: limit,
      stopLossPrice: stopLoss.trim() === '' ? null : Number(stopLoss),
      targetPrice: target.trim() === '' ? null : Number(target),
    })
  }

  const unitWord = instrument?.quantityUnit ?? 'lots'
  const units = instrument && qtyValid ? qty * instrument.lotSize : null

  return (
    <div className="page">
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
        <div className="form-row">
          <div className="field">
            <label className="field__label" htmlFor="manual-symbol">
              Instrument
            </label>
            <SymbolCombobox
              id="manual-symbol"
              value={symbol}
              onChange={setSymbol}
              placeholder="MCX:CRUDEOIL26SEPFUT, NSE:SBIN-EQ, NSE:BANKNIFTY26SEP56800CE"
            />
            <p className="field__help">Commodity, equity or option — search by name or symbol.</p>
          </div>
        </div>

        {instrumentQuery.isError && <InlineError error={instrumentQuery.error} />}

        {symbol.trim() !== '' && instrumentQuery.isLoading && <Loading label="Reading the book…" />}

        {instrument && (
          <>
            <InstrumentRules instrument={instrument} />
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
                  {instrument.subscribing
                    ? 'Just added to the live feed — the first tick usually lands within a few seconds. '
                    : 'No live price yet. '}
                  The market may also be closed for this segment. You can still place a Limit order.
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
                  value={quantity}
                  onChange={(e) => setQuantity(e.target.value)}
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
                    className={`seg__btn ${orderType === 'market' ? 'is-active' : ''}`}
                    onClick={() => setOrderType('market')}
                  >
                    Market
                  </button>
                  <button
                    type="button"
                    className={`seg__btn ${orderType === 'limit' ? 'is-active' : ''}`}
                    onClick={() => setOrderType('limit')}
                  >
                    Limit
                  </button>
                </div>

                {orderType === 'limit' ? (
                  <>
                    <input
                      id="manual-limit"
                      className="field__input"
                      style={{ marginTop: 8 }}
                      type="number"
                      step={instrument.tickSize ?? 0.05}
                      value={limitPrice}
                      onChange={(e) => setLimitPrice(e.target.value)}
                      placeholder={String(instrument.buyAt ?? instrument.ltp ?? '')}
                    />
                    <p className={`field__help ${offTick || limitMissing ? 'neg' : ''}`}>
                      {limitMissing
                        ? 'Enter a limit price, or switch to Market.'
                        : offTick
                          ? `Not a multiple of the ${instrument.tickSize} tick.`
                          : `Must be a multiple of ${instrument.tickSize ?? '—'}.`}
                    </p>
                  </>
                ) : (
                  <p className="field__help">
                    Takes the book: buys at the ask {instrument.ask ?? '—'}, sells at the bid{' '}
                    {instrument.bid ?? '—'}.
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
                  value={stopLoss}
                  onChange={(e) => setStopLoss(e.target.value)}
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
                  value={target}
                  onChange={(e) => setTarget(e.target.value)}
                  placeholder="this position only"
                />
                <p className="field__help">Above the fill for a buy, below it for a sell.</p>
              </div>
            </div>

            <div className="row" style={{ gap: 10, marginTop: 6, flexWrap: 'wrap' }}>
              <button
                type="button"
                className="btn btn--pos"
                disabled={!canPlace || (limit == null && instrument.buyAt == null)}
                onClick={() => submit('BUY')}
              >
                {place.isPending ? 'Placing…' : `Buy at ${limit ?? instrument.buyAt ?? '—'}`}
              </button>
              <button
                type="button"
                className="btn btn--danger"
                disabled={!canPlace || (limit == null && instrument.sellAt == null)}
                onClick={() => submit('SELL')}
              >
                {place.isPending ? 'Placing…' : `Sell at ${limit ?? instrument.sellAt ?? '—'}`}
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
        {book.isLoading ? (
          <Loading label="Opening your book…" />
        ) : book.data?.runId ? (
          <RunCard
            strategy={{ name: 'Manual', category: 'Manual' }}
            runId={book.data.runId}
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
