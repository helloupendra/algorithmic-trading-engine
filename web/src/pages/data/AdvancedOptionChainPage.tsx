/**
 * The option chain: every strike of one expiry, priced, with the open interest
 * written behind it and how that has moved since the session opened.
 *
 * Laid out the way a chain is read rather than the way a table is easiest to
 * build — calls falling away to the left, puts to the right, the strike column
 * down the middle, and the money marked. The eye goes to the middle and works
 * outward, so that is where the alignment has to hold.
 *
 * Every number carries a bar behind it scaled against the largest value in its
 * own column. On a chain the absolute figures are meaningless at a glance —
 * nobody reads "1,94,000" and knows whether that is a lot — but "the longest
 * bar in the column" is legible instantly, and that is what a trader is
 * actually looking for.
 *
 * Live and replay are the same component: pass `asOfUtc` and it shows the chain
 * that was true at that moment, which is what the backtest player does as its
 * clock advances.
 */

import { useMemo, useState } from 'react'
import {
  useLiveOptionChain,
  useOptionChainExpiries,
} from '../../lib/queries'
import type { OptionChain, OptionChainLeg, OptionChainStrike } from '../../lib/types'
import { EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

const UNDERLYINGS = ['BANKNIFTY', 'NIFTY', 'FINNIFTY', 'MIDCPNIFTY', 'SENSEX']

/** Compact Indian-market notation: 1.94L, 44.40K. */
function compact(value: number | null | undefined): string {
  if (value == null) return '—'
  const abs = Math.abs(value)
  if (abs >= 1e7) return `${(value / 1e7).toFixed(2)}Cr`
  if (abs >= 1e5) return `${(value / 1e5).toFixed(2)}L`
  if (abs >= 1e3) return `${(value / 1e3).toFixed(2)}K`
  return String(Math.round(value))
}

function price(value: number | null | undefined): string {
  return value == null ? '—' : value.toFixed(2)
}

const BUILD_UP_LABEL: Record<string, string> = {
  LongBuildUp: 'Long build',
  ShortBuildUp: 'Short build',
  ShortCovering: 'Short cover',
  LongUnwinding: 'Long unwind',
  Neutral: '',
}

/** Long/short build read as a position being opened; covering/unwinding as one closing. */
const BUILD_UP_TONE: Record<string, string> = {
  LongBuildUp: 'oc-build-long',
  ShortBuildUp: 'oc-build-short',
  ShortCovering: 'oc-build-cover',
  LongUnwinding: 'oc-build-unwind',
  Neutral: '',
}

/** The largest absolute value in a column, so bars can be scaled against it. */
function columnPeak(strikes: OptionChainStrike[], pick: (leg: OptionChainLeg | null) => number | null): number {
  let peak = 0
  for (const strike of strikes) {
    for (const leg of [strike.call, strike.put]) {
      const value = Math.abs(pick(leg) ?? 0)
      if (value > peak) peak = value
    }
  }
  return peak
}

function Cell({
  value,
  peak,
  align,
  format,
  tone,
}: {
  value: number | null | undefined
  peak: number
  align: 'left' | 'right'
  format: (v: number | null | undefined) => string
  tone?: 'call' | 'put'
}) {
  // The bar is the point of the cell; the number is the detail you read after.
  const share = peak > 0 && value != null ? Math.min(1, Math.abs(value) / peak) : 0

  // Open interest falling is a different event from it rising — positions being
  // closed rather than opened — so it must not read the same at a glance.
  const negative = (value ?? 0) < 0

  return (
    <td className={`oc-cell oc-cell--${align}`}>
      <span
        className={`oc-bar oc-bar--${align} ${tone ? `oc-bar--${tone}` : ''} ${negative ? 'oc-bar--negative' : ''}`}
        style={{ width: `${(share * 100).toFixed(1)}%` }}
        aria-hidden="true"
      />
      <span className={`oc-value ${negative ? 'oc-value--negative' : ''}`}>{format(value)}</span>
    </td>
  )
}

function ChainTable({ chain }: { chain: OptionChain }) {
  const peaks = useMemo(
    () => ({
      oi: columnPeak(chain.strikes, (l) => l?.openInterest ?? null),
      oiChange: columnPeak(chain.strikes, (l) => l?.openInterestChange ?? null),
      volume: columnPeak(chain.strikes, (l) => l?.volume ?? null),
    }),
    [chain.strikes],
  )

  if (chain.strikes.length === 0) {
    return (
      <EmptyState>
        No chain has been captured for {chain.underlying} yet. Open interest is only recorded while
        the chain poller runs — start it from Data → Live feeds.
      </EmptyState>
    )
  }

  return (
    <div className="tablewrap tablewrap--tall">
      <table className="table oc-table">
        <thead>
          <tr>
            <th colSpan={5} className="oc-side oc-side--call">Calls</th>
            <th className="oc-strike-head">Strike</th>
            <th colSpan={5} className="oc-side oc-side--put">Puts</th>
          </tr>
          <tr>
            <th>Analysis</th>
            <th className="r">OI</th>
            <th className="r">OI chg</th>
            <th className="r">Volume</th>
            <th className="r">LTP</th>
            <th className="oc-strike-head r">PCR</th>
            <th>LTP</th>
            <th>Volume</th>
            <th>OI chg</th>
            <th>OI</th>
            <th>Analysis</th>
          </tr>
        </thead>
        <tbody>
          {chain.strikes.map((strike) => (
            <tr
              key={strike.strikePrice}
              className={strike.isAtTheMoney ? 'oc-row oc-row--atm' : 'oc-row'}
            >
              <td className={`oc-analysis ${BUILD_UP_TONE[strike.call?.buildUp ?? 'Neutral'] ?? ''}`}>
                {BUILD_UP_LABEL[strike.call?.buildUp ?? 'Neutral'] ?? ''}
              </td>
              <Cell value={strike.call?.openInterest} peak={peaks.oi} align="right" format={compact} tone="call" />
              <Cell value={strike.call?.openInterestChange} peak={peaks.oiChange} align="right" format={compact} tone="call" />
              <Cell value={strike.call?.volume} peak={peaks.volume} align="right" format={compact} tone="call" />
              <td className="oc-cell oc-cell--right oc-ltp">{price(strike.call?.lastTradedPrice)}</td>

              <td className="oc-strike">
                <b>{strike.strikePrice}</b>
                <span className="oc-pcr">{strike.putCallRatio?.toFixed(2) ?? '—'}</span>
              </td>

              <td className="oc-cell oc-cell--left oc-ltp">{price(strike.put?.lastTradedPrice)}</td>
              <Cell value={strike.put?.volume} peak={peaks.volume} align="left" format={compact} tone="put" />
              <Cell value={strike.put?.openInterestChange} peak={peaks.oiChange} align="left" format={compact} tone="put" />
              <Cell value={strike.put?.openInterest} peak={peaks.oi} align="left" format={compact} tone="put" />
              <td className={`oc-analysis ${BUILD_UP_TONE[strike.put?.buildUp ?? 'Neutral'] ?? ''}`}>
                {BUILD_UP_LABEL[strike.put?.buildUp ?? 'Neutral'] ?? ''}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ChainHeader({ chain }: { chain: OptionChain }) {
  return (
    <div className="oc-header">
      <div className="oc-head-item">
        <span className="oc-head-label">Spot</span>
        <span className="oc-head-value">{chain.spotPrice.toFixed(2)}</span>
      </div>
      <div className="oc-head-item">
        <span className="oc-head-label">ATM</span>
        <span className="oc-head-value">{chain.atTheMoneyStrike ?? '—'}</span>
      </div>
      <div className="oc-head-item">
        <span className="oc-head-label">Max pain</span>
        <span className="oc-head-value">{chain.maxPainStrike ?? '—'}</span>
      </div>
      <div className="oc-head-item">
        <span className="oc-head-label">PCR</span>
        <span className="oc-head-value">{chain.putCallRatio?.toFixed(2) ?? '—'}</span>
      </div>
      <div className="oc-head-item">
        <span className="oc-head-label">Heaviest call</span>
        <span className="oc-head-value">{chain.heaviestCallStrike ?? '—'}</span>
      </div>
      <div className="oc-head-item">
        <span className="oc-head-label">Heaviest put</span>
        <span className="oc-head-value">{chain.heaviestPutStrike ?? '—'}</span>
      </div>
      <div className="oc-head-item oc-head-item--wide">
        <span className="oc-head-label">As of</span>
        <span className="oc-head-value mono">
          {new Date(chain.asOfUtc).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata' })}
        </span>
      </div>
    </div>
  )
}

export function AdvancedOptionChainPage({ asOfUtc }: { asOfUtc?: string } = {}) {
  const [underlying, setUnderlying] = useState('BANKNIFTY')
  const [expiry, setExpiry] = useState<string | undefined>(undefined)

  const expiries = useOptionChainExpiries(underlying)
  const chain = useLiveOptionChain(underlying, expiry, asOfUtc)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Option chain</h1>
        <p className="page__subtitle">
          Every strike of one expiry, with the open interest written behind it. Bars are scaled
          against the largest value in their own column — the absolute figures mean little at a
          glance, the longest bar means a great deal.
        </p>
      </header>

      <div className="chip-row" style={{ marginBottom: 12 }}>
        {UNDERLYINGS.map((name) => (
          <button
            key={name}
            type="button"
            className={`btn btn--sm ${underlying === name ? 'btn--primary' : 'btn--ghost'}`}
            onClick={() => {
              setUnderlying(name)
              setExpiry(undefined)
            }}
          >
            {name}
          </button>
        ))}

        <span style={{ flex: 1 }} />

        <QueryBoundary query={expiries}>
          {(list) =>
            list.length === 0 ? (
              <span className="muted small-note">no expiry captured yet</span>
            ) : (
              <select
                className="field__input"
                value={expiry ?? list[0]}
                onChange={(e) => setExpiry(e.target.value)}
                aria-label="Expiry"
              >
                {list.map((d) => (
                  <option key={d} value={d}>
                    {d}
                  </option>
                ))}
              </select>
            )
          }
        </QueryBoundary>
      </div>

      {chain.isError && <InlineError error={chain.error} />}

      <QueryBoundary query={chain}>
        {(data) => (
          <>
            <ChainHeader chain={data} />

            {data.openInterestUnavailable && data.strikes.length > 0 && (
              <p className="small-note warn" style={{ marginTop: 8 }}>
                No open interest for this period. The broker's tick feed does not carry it, so it
                exists only where the chain poller was running — and it cannot be filled in
                afterwards. Prices and volume below are real; the OI columns are blank rather than
                zero, because zero would read as "nothing is written here".
              </p>
            )}

            <Panel title={`${data.underlying} · ${data.expiryDate || 'nearest expiry'}`}>
              <ChainTable chain={data} />
            </Panel>
          </>
        )}
      </QueryBoundary>
    </div>
  )
}
