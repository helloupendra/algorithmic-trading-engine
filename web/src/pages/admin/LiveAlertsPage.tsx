/**
 * Live Alerts — controls the Telegram alerter daemon and fires end-to-end
 * test signals through the whole pipeline (API → Redis → python engine →
 * Telegram). Rewritten on the v2 vocabulary; behavior unchanged.
 */

import { useState, useEffect, useRef } from 'react'
import { Badge, Panel } from '../../components/ui'
import { api } from '../../lib/api'
import {
  useAlerterLogs,
  useAlerterStatus,
  useLatestQuotes,
  useStartAlerter,
  useStopAlerter,
} from '../../lib/queries'
import { formatAge, formatPrice } from '../../lib/format'
import { IconBell, IconPlay, IconStop } from '../../components/icons'

interface E2EResponse {
  status?: string
  message?: string
  broadcastedPayload?: unknown
  [key: string]: unknown
}

interface LocalLog {
  time: string
  msg: string
  type: 'info' | 'success' | 'error'
}

const CORE_INDICES = ['NSE:NIFTY50-INDEX', 'NSE:NIFTYBANK-INDEX', 'BSE:SENSEX-INDEX']

const TARGETS = [
  { value: 'BSE:SENSEX-INDEX', label: 'SENSEX' },
  { value: 'NSE:NIFTY50-INDEX', label: 'NIFTY50' },
  { value: 'NSE:BANKNIFTY-INDEX', label: 'BANKNIFTY' },
  { value: 'NSE:HDFCBANK-EQ', label: 'HDFCBANK' },
  { value: 'NSE:RELIANCE-EQ', label: 'RELIANCE' },
]

function Console({
  title,
  lines,
  short,
  emptyText,
}: {
  title: string
  lines: React.ReactNode
  short?: boolean
  emptyText?: boolean
}) {
  return (
    <div className="console">
      <div className="console__bar">
        <span className="console__dot console__dot--r" />
        <span className="console__dot console__dot--y" />
        <span className="console__dot console__dot--g" />
        <span className="console__title">{title}</span>
      </div>
      <div className={`console__body ${short ? 'console__body--short' : ''} ${emptyText ? 'faint' : ''}`}>
        {lines}
      </div>
    </div>
  )
}

export function LiveAlertsPage() {
  const statusQuery = useAlerterStatus()
  const logsQuery = useAlerterLogs()
  const startAlerter = useStartAlerter()
  const stopAlerter = useStopAlerter()
  const quotesQuery = useLatestQuotes()

  const daemonLogsRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const el = daemonLogsRef.current?.querySelector('.console__body')
    if (el) el.scrollTop = el.scrollHeight
  }, [logsQuery.data])

  const [instrument, setInstrument] = useState('BSE:SENSEX-INDEX')
  const [loading, setLoading] = useState(false)
  const [logs, setLogs] = useState<LocalLog[]>([])

  const appendLog = (msg: string, type: LocalLog['type'] = 'info') =>
    setLogs((prev) => [...prev, { time: new Date().toLocaleTimeString(), msg, type }])

  async function handleTestAlert() {
    setLoading(true)
    appendLog(`Broadcasting E2E command for ${instrument} to the .NET backend…`)
    try {
      const data = await api.post<E2EResponse>('/api/alerts/test-e2e', { instrument })
      if (data.status === 'success') {
        appendLog('Received 200 OK from the API.', 'success')
        appendLog(`Response: ${JSON.stringify(data, null, 2)}`, 'success')
      } else {
        appendLog(`Error from API: ${data.message || JSON.stringify(data)}`, 'error')
      }
    } catch (error) {
      appendLog(`Request failed: ${error instanceof Error ? error.message : String(error)}`, 'error')
    }
    setLoading(false)
  }

  const running = statusQuery.data?.isRunning ?? false

  // Defensive: an API build without this route answers with the SPA fallback
  // HTML (a string), which must not crash the page.
  const daemonLines = Array.isArray(logsQuery.data) ? logsQuery.data : []

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">
            Live Alerts{' '}
            {running ? <Badge tone="pos">running</Badge> : <Badge tone="neutral">stopped</Badge>}
          </h1>
          <p className="page__subtitle">
            End-to-end simulation and trigger for real-time Telegram signals.
            {running && statusQuery.data?.startedUtc && (
              <> Daemon started {formatAge(statusQuery.data.startedUtc)}.</>
            )}
          </p>
        </div>
      </header>

      <div className="stat-grid">
        {CORE_INDICES.map((symbol) => {
          const quote = quotesQuery.data?.find((q) => q.symbol === symbol)
          return (
            <div className="stat" key={symbol}>
              <div className="stat__value mono">{formatPrice(quote?.lastTradedPrice)}</div>
              <div className="stat__label">{symbol.split(':')[1]}</div>
              <div className="stat__sub">
                {quote ? `updated ${formatAge(quote.updatedUtc)}` : 'no live quote'}
              </div>
            </div>
          )
        })}
      </div>

      <Panel
        title={
          <>
            <IconBell /> Daemon controls
          </>
        }
        actions={
          running ? (
            <button
              className="btn btn--danger btn--sm"
              onClick={() => stopAlerter.mutate()}
              disabled={stopAlerter.isPending}
            >
              <IconStop style={{ width: 13, height: 13 }} />
              {stopAlerter.isPending ? 'Stopping…' : 'Stop daemon'}
            </button>
          ) : (
            <button
              className="btn btn--pos btn--sm"
              onClick={() => startAlerter.mutate()}
              disabled={startAlerter.isPending}
            >
              <IconPlay style={{ width: 13, height: 13 }} />
              {startAlerter.isPending ? 'Starting…' : 'Start daemon'}
            </button>
          )
        }
      >
        <p className="card__muted">
          Runs the background python engine that watches the market and sends live alerts to
          Telegram.
        </p>
        <div ref={daemonLogsRef}>
          <Console
            title="Python daemon console"
            emptyText={daemonLines.length === 0}
            lines={
              daemonLines.length === 0
                ? 'No output from the daemon yet…'
                : daemonLines.map((line, i) => <div key={i}>{line}</div>)
            }
          />
        </div>
      </Panel>

      <Panel title="E2E alert trigger">
        <div className="inline-form" style={{ marginBottom: 14 }}>
          <label className="field__label" htmlFor="e2e-target">
            Target instrument
          </label>
          <select
            id="e2e-target"
            className="field__input field__input--sm"
            value={instrument}
            onChange={(e) => setInstrument(e.target.value)}
          >
            {TARGETS.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
          <button className="btn btn--primary btn--sm" onClick={handleTestAlert} disabled={loading}>
            {loading ? 'Sending…' : 'Trigger alert'}
          </button>
        </div>

        <Console
          title="Terminal session"
          short
          emptyText={logs.length === 0}
          lines={
            logs.length === 0
              ? 'Waiting for command execution…'
              : logs.map((log, i) => (
                  <div
                    key={i}
                    className={log.type === 'error' ? 'neg' : log.type === 'success' ? 'pos' : ''}
                  >
                    <span className="console__line--time">[{log.time}]</span>
                    {log.msg}
                  </div>
                ))
          }
        />
      </Panel>
    </div>
  )
}
