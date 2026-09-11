/**
 * The parts of TrueData that are its own, on its connector page.
 *
 * Everything a connector shares with the others — credentials, session,
 * capabilities, routing — is already on that page. Two things are not, because
 * no generic page can know to ask for them: the symbol master, without which
 * futures and monthly options have no name this vendor understands, and the
 * option chain, which arrives here with open interest and greeks the FYERS
 * subscription does not carry.
 */

import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { InlineError, Panel } from '../../components/ui'

interface ImportSegment {
  segment: string
  rowsRead: number
  mapped: number
  skipped: number
  error?: string | null
}

interface ImportResult {
  segments: ImportSegment[]
  mapped: number
  rowsRead: number
  failed: string[]
}

interface ChainSide {
  lastPrice: number | null
  bid: number | null
  ask: number | null
  openInterest: number | null
  openInterestChange: number | null
  volume: number | null
  greeks: { delta: number | null; theta: number | null; vega: number | null; gamma: number | null; impliedVolatility: number | null } | null
}

interface ChainResult {
  underlying: string
  expiry: string
  strikes: number
  peakCallOiStrike: number | null
  peakPutOiStrike: number | null
  putCallOiRatio: number | null
  rows: { strike: number; call: ChainSide; put: ChainSide }[]
}

const num = (v: number | null | undefined, digits = 2) =>
  v === null || v === undefined ? '—' : v.toLocaleString('en-IN', { maximumFractionDigits: digits })

export function TrueDataPanel() {
  return (
    <>
      <SymbolMasterPanel />
      <ChainPanel />
    </>
  )
}

function SymbolMasterPanel() {
  const [search, setSearch] = useState('')

  const run = useMutation({
    mutationFn: () =>
      api.post<ImportResult>(
        `/api/TrueData/symbols/import${search.trim() ? `?search=${encodeURIComponent(search.trim())}` : ''}`,
      ),
  })

  return (
    <Panel title="Symbol master">
      <p className="muted" style={{ maxWidth: '80ch', marginTop: 0 }}>
        TrueData names a future by how far out it is — <code className="mono">CRUDEOIL-I</code> is
        whichever contract is nearest today — and every option by its exact expiry day. Neither can be
        worked out from this platform's own symbol, so the master is downloaded and its name for each
        contract recorded. Indices and equities need no rows: those translate on their own.
      </p>
      <p className="muted" style={{ maxWidth: '80ch' }}>
        Run it once a day before the open. Leave the filter empty for the whole F&amp;O, MCX and BSE
        F&amp;O lists, or narrow it to test.
      </p>

      <div className="inline-form" style={{ gap: 8, flexWrap: 'wrap' }}>
        <input
          className="input"
          style={{ maxWidth: 220 }}
          placeholder="Filter, e.g. NIFTY (optional)"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <button className="btn btn--primary" disabled={run.isPending} onClick={() => run.mutate()}>
          {run.isPending ? 'Importing…' : 'Import symbol master'}
        </button>
      </div>

      {run.isError && <InlineError error={run.error} />}

      {run.data && (
        <div className="tablewrap" style={{ marginTop: 12 }}>
          <table className="table">
            <thead>
              <tr>
                <th>Segment</th>
                <th className="num">Rows</th>
                <th className="num">Mapped</th>
                <th className="num">Skipped</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {run.data.segments.map((s) => (
                <tr key={s.segment}>
                  <td className="mono">{s.segment}</td>
                  <td className="num">{num(s.rowsRead, 0)}</td>
                  <td className="num">{num(s.mapped, 0)}</td>
                  <td className="num">{num(s.skipped, 0)}</td>
                  <td className={s.error ? 'neg' : 'pos'}>{s.error ?? 'ok'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function ChainPanel() {
  const [underlying, setUnderlying] = useState('NIFTY')
  const [expiry, setExpiry] = useState('')

  const load = useMutation({
    mutationFn: () =>
      api.get<ChainResult>(
        `/api/TrueData/chain?underlying=${encodeURIComponent(underlying)}&expiry=${encodeURIComponent(expiry)}`,
      ),
  })

  const chain = load.data

  return (
    <Panel title="Option chain, open interest and greeks">
      <p className="muted" style={{ maxWidth: '80ch', marginTop: 0 }}>
        One call returns both sides of every strike: last price, best bid and ask with their sizes,
        open interest and the previous day's, volume, and delta, theta, vega, gamma and implied
        volatility. This is the feed's own pricing, not the platform's — worth comparing against the
        greeks calculated here, and the only source of open interest this desk has.
      </p>

      <div className="inline-form" style={{ gap: 8, flexWrap: 'wrap' }}>
        <input
          className="input"
          style={{ maxWidth: 160 }}
          placeholder="NIFTY"
          value={underlying}
          onChange={(e) => setUnderlying(e.target.value.toUpperCase())}
        />
        <input
          className="input"
          style={{ maxWidth: 170 }}
          type="date"
          value={expiry}
          onChange={(e) => setExpiry(e.target.value)}
        />
        <button
          className="btn btn--primary"
          disabled={load.isPending || !underlying.trim() || !expiry}
          onClick={() => load.mutate()}
        >
          {load.isPending ? 'Loading…' : 'Load chain'}
        </button>
      </div>

      {load.isError && <InlineError error={load.error} />}

      {chain && (
        <>
          <p className="inline-form" style={{ gap: 14, marginTop: 12, fontSize: 12.5, flexWrap: 'wrap' }}>
            <span><b>{chain.strikes}</b> strikes</span>
            <span>peak call OI <b className="mono">{num(chain.peakCallOiStrike, 0)}</b></span>
            <span>peak put OI <b className="mono">{num(chain.peakPutOiStrike, 0)}</b></span>
            <span>put/call OI <b className="mono">{num(chain.putCallOiRatio, 4)}</b></span>
          </p>

          <div className="tablewrap tablewrap--rows8">
            <table className="table table--compact">
              <thead>
                <tr>
                  <th colSpan={5} style={{ textAlign: 'center' }}>Calls</th>
                  <th className="num">Strike</th>
                  <th colSpan={5} style={{ textAlign: 'center' }}>Puts</th>
                </tr>
                <tr>
                  <th className="num">OI</th>
                  <th className="num">ΔOI</th>
                  <th className="num">Vol</th>
                  <th className="num">IV</th>
                  <th className="num">LTP</th>
                  <th className="num" />
                  <th className="num">LTP</th>
                  <th className="num">IV</th>
                  <th className="num">Vol</th>
                  <th className="num">ΔOI</th>
                  <th className="num">OI</th>
                </tr>
              </thead>
              <tbody>
                {chain.rows.map((r) => (
                  <tr key={r.strike}>
                    <td className="num">{num(r.call.openInterest, 0)}</td>
                    <td className={`num ${(r.call.openInterestChange ?? 0) >= 0 ? 'pos' : 'neg'}`}>
                      {num(r.call.openInterestChange, 0)}
                    </td>
                    <td className="num">{num(r.call.volume, 0)}</td>
                    <td className="num">{num(r.call.greeks?.impliedVolatility, 4)}</td>
                    <td className="num">{num(r.call.lastPrice)}</td>
                    <td className="num mono"><b>{num(r.strike, 0)}</b></td>
                    <td className="num">{num(r.put.lastPrice)}</td>
                    <td className="num">{num(r.put.greeks?.impliedVolatility, 4)}</td>
                    <td className="num">{num(r.put.volume, 0)}</td>
                    <td className={`num ${(r.put.openInterestChange ?? 0) >= 0 ? 'pos' : 'neg'}`}>
                      {num(r.put.openInterestChange, 0)}
                    </td>
                    <td className="num">{num(r.put.openInterest, 0)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </Panel>
  )
}
