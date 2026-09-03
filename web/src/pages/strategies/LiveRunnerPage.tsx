import React, { useState } from 'react'
import { IconPlay, IconStop, IconBot, IconChevronDown, IconChevronUp } from '../../components/icons'
import { Panel, QueryBoundary, Badge } from '../../components/ui'
import { useStrategies, useStartStrategy, useStopStrategy, useStrategySignals, useLatestQuotes } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'

function StrategyRow({ s }: { s: any }) {
  const start = useStartStrategy()
  const stop = useStopStrategy()
  const [expanded, setExpanded] = useState(false)
  const signalsQuery = useStrategySignals(s.id, s.isActive && expanded)
  const quotesQuery = useLatestQuotes()

  const getQuote = (symbol: string) => {
    return quotesQuery.data?.find(q => q.symbol === symbol)
  }

  return (
    <React.Fragment>
      <tr>
        <td>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <button 
              className="btn btn--icon" 
              onClick={() => setExpanded(!expanded)}
              style={{ background: 'transparent', padding: 0, opacity: 0.7 }}
            >
              {expanded ? <IconChevronUp width={16} height={16} /> : <IconChevronDown width={16} height={16} />}
            </button>
            <IconBot style={{ width: 18, height: 18, opacity: 0.7, flexShrink: 0 }} />
            <strong>{s.name}</strong>
          </div>
        </td>
        <td>
          {s.isActive ? (
            <Badge tone="pos">Running</Badge>
          ) : (
            <Badge>Stopped</Badge>
          )}
        </td>
        <td>{s.isActive ? s.startedBy : '-'}</td>
        <td>{s.isActive && s.startedUtc ? formatDateTime(s.startedUtc) : '-'}</td>
        <td className="text-right">
          {s.isActive ? (
            <button
              type="button"
              className="btn btn--sm btn--neg"
              onClick={() => {
                if (window.confirm(`Stop ${s.name}?`)) stop.mutate(s.id)
              }}
              disabled={stop.isPending}
            >
              <IconStop style={{ width: 14, height: 14 }} /> Stop
            </button>
          ) : (
            <button
              type="button"
              className="btn btn--sm btn--pos"
              onClick={() => start.mutate(s.id)}
              disabled={start.isPending}
            >
              <IconPlay style={{ width: 14, height: 14 }} /> Start
            </button>
          )}
        </td>
      </tr>
      
      {expanded && (
        <tr>
          <td colSpan={5} style={{ background: 'var(--bg-card-alt)', padding: '16px' }}>
            <h4 style={{ margin: '0 0 12px 0', fontSize: '13px', textTransform: 'uppercase', letterSpacing: '0.05em', opacity: 0.7 }}>
              Live Signals Feed
            </h4>
            {!s.isActive ? (
              <div className="empty-state" style={{ padding: '24px 0', fontSize: '14px' }}>Start the strategy to see live signals.</div>
            ) : signalsQuery.isLoading ? (
              <div style={{ padding: '12px 0', opacity: 0.6 }}>Loading signals...</div>
            ) : Array.isArray(signalsQuery.data) && signalsQuery.data.length > 0 ? (
              <div style={{ maxHeight: '400px', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '12px', paddingRight: '8px' }}>
                {signalsQuery.data.map((sig, idx) => {
                  const isPos = sig?.signal_type?.includes('BUY') || sig?.signal_type?.includes('OPEN')
                  return (
                    <div key={idx} style={{ flexShrink: 0, background: '#ffffff', borderRadius: '8px', overflow: 'hidden', border: '1px solid #e2e8f0', color: '#1e293b', boxShadow: '0 1px 3px rgba(0,0,0,0.1)' }}>
                      <div style={{ padding: '8px 16px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', background: '#f8fafc', borderBottom: '1px solid #e2e8f0' }}>
                        <div style={{ fontSize: '12px', fontWeight: 600, color: '#64748b' }}>
                          {sig.timestamp_utc ? new Date(sig.timestamp_utc).toLocaleTimeString() : 'N/A'} • {sig.signal_type || 'UNKNOWN'}
                        </div>
                      </div>
                      <div style={{ padding: '0 16px' }}>
                        {Array.isArray(sig.legs) && sig.legs.map((leg: any, lidx: number) => {
                          const sideColor = leg.side === 'BUY' ? '#10b981' : '#ef4444'
                          const quote = getQuote(leg.symbol) as any
                          
                          const ltp = quote?.lastTradedPrice || 0
                          const entryPrice = leg.price || ltp
                          const quantity = leg.quantity || 1
                          
                          let pnl = 0
                          if (ltp > 0 && entryPrice > 0) {
                            if (leg.side === 'BUY') pnl = (ltp - entryPrice) * quantity
                            else pnl = (entryPrice - ltp) * quantity
                          }
                          
                          const iv = (quote?.impliedVolatility || 0).toFixed(2)
                          const delta = (quote?.delta || 0).toFixed(2)
                          const gamma = (quote?.gamma || 0).toFixed(4)
                          const theta = (quote?.theta || 0).toFixed(2)
                          const vega = (quote?.vega || 0).toFixed(2)

                          return (
                            <div key={lidx} style={{ padding: '12px 0', borderBottom: lidx < sig.legs.length - 1 ? '1px solid #f1f5f9' : 'none', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                              <div>
                                <div style={{ fontSize: '14px', fontWeight: 600, marginBottom: '6px', display: 'flex', gap: '8px', alignItems: 'center', color: '#0f172a' }}>
                                  <span style={{ color: '#64748b' }}>{lidx + 1}.</span>
                                  <span>{leg?.symbol?.replace('NSE:', '') || leg?.symbol}</span>
                                  <span style={{ color: sideColor, fontSize: '12px', padding: '2px 6px', background: `${sideColor}15`, borderRadius: '4px' }}>{leg.side} {leg.quantity}x</span>
                                </div>
                                <div style={{ fontSize: '11px', color: '#94a3b8', display: 'flex', gap: '8px', fontWeight: 500 }}>
                                  <span>IV: {iv}%</span>
                                  <span style={{ color: '#cbd5e1' }}>|</span>
                                  <span>Delta: {delta}</span>
                                  <span style={{ color: '#cbd5e1' }}>|</span>
                                  <span>Gamma: {gamma}</span>
                                  <span style={{ color: '#cbd5e1' }}>|</span>
                                  <span>Theta: {theta}</span>
                                  <span style={{ color: '#cbd5e1' }}>|</span>
                                  <span>Vega: {vega}</span>
                                </div>
                              </div>
                              <div style={{ display: 'flex', gap: '24px', alignItems: 'center', textAlign: 'right', fontSize: '14px', fontWeight: 500 }}>
                                <div style={{ width: '40px', color: '#64748b' }} title="Quantity">{quantity}</div>
                                <div style={{ width: '60px', color: '#64748b' }} title="Entry Price">{entryPrice.toFixed(2)}</div>
                                <div style={{ width: '60px', color: '#64748b' }} title="LTP">{ltp.toFixed(2)}</div>
                                <div style={{ width: '80px', color: pnl > 0 ? '#10b981' : pnl < 0 ? '#ef4444' : '#64748b', fontWeight: 700 }} title="PnL">
                                  {pnl > 0 ? '+' : ''}{pnl.toFixed(2)}
                                </div>
                              </div>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )
                })}
              </div>
            ) : (
              <div style={{ padding: '24px 0', display: 'flex', alignItems: 'center', gap: '12px', opacity: 0.8 }}>
                <span style={{ width: '8px', height: '8px', background: 'var(--color-pos)', borderRadius: '50%', boxShadow: '0 0 8px var(--color-pos)' }}></span>
                <span style={{ fontSize: '14px' }}>Strategy started successfully. Monitoring real-time market data for entry conditions...</span>
              </div>
            )}
          </td>
        </tr>
      )}
    </React.Fragment>
  )
}

function RunnerList() {
  const strategies = useStrategies()
  const start = useStartStrategy()
  const stop = useStopStrategy()
  
  const data = strategies.data ?? []

  if (data.length === 0) {
    return <div className="empty-state">No strategies available to run.</div>
  }

  return (
    <div className="table-wrapper">
      <table className="table">
        <thead>
          <tr>
            <th>Strategy Name</th>
            <th>Status</th>
            <th>Started By</th>
            <th>Started At</th>
            <th className="text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {data.map((s) => (
            <StrategyRow key={s.id} s={s} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function LiveRunnerPage() {
  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Live Runner</h1>
          <p className="page__subtitle">
            Deploy and manage live running instances of your strategies.
          </p>
        </div>
      </header>

      <Panel
        title={
          <>
            <IconPlay /> Active Processes
          </>
        }
      >
        <QueryBoundary query={useStrategies()}>
          {() => <RunnerList />}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
