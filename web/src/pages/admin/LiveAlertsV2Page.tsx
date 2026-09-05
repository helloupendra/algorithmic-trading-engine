/**
 * Alerts — the platform's one notification channel.
 *
 * Everything the platform wants to tell its operator takes the same path:
 * Redis `alerts:new` → Telegram (when configured) → the `alert_events` table.
 * That means the stream below is a complete history, not a selection, and an
 * event is never delivered without also being recorded.
 *
 * Lives under the System module, because "is the platform behaving" is one
 * question, not three.
 */

import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { useAlertEvents } from '../../lib/queries'
import { formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

interface AlerterProcess {
  underlying: string
  processId: number | null
  source: string
  startedUtc: string | null
}

interface AlerterStatus {
  isRunning: boolean
  managed: boolean
  processes: AlerterProcess[]
  telegramConfigured: boolean
}

/** What the platform sends without anyone asking it to. */
const WHAT_GETS_SENT: { label: string; detail: string }[] = [
  { label: 'Strategy run started', detail: 'name, underlying, lots and who started it' },
  { label: 'Strategy run stopped', detail: 'who stopped it and how many positions were squared off' },
  { label: 'Kill switch', detail: 'activated or released, with the reason' },
  { label: 'Strategy signals', detail: 'from the alerter process below, per underlying' },
]

function severityTone(severity: string): 'pos' | 'neg' | 'warn' | 'accent' | 'neutral' {
  switch (severity?.toLowerCase()) {
    case 'error':
    case 'critical':
      return 'neg'
    case 'warning':
      return 'warn'
    case 'success':
      return 'pos'
    case 'info':
      return 'accent'
    default:
      return 'neutral'
  }
}

function useAlerterStatus() {
  return useQuery({
    queryKey: ['alerts', 'status'],
    queryFn: () => api.get<AlerterStatus>('/api/Alerts/status'),
    refetchInterval: 15_000,
  })
}

function DeliveryPanel({ status }: { status: AlerterStatus }) {
  return (
    <Panel title="Telegram delivery">
      <div className="broker-state">
        <div>
          <div className={`killswitch__state ${status.telegramConfigured ? 'pos' : 'warn'}`}>
            {status.telegramConfigured ? 'Configured' : 'Not configured'}
          </div>
          <p className="muted" style={{ maxWidth: '78ch' }}>
            {status.telegramConfigured ? (
              <>
                A bot token and chat id are set on the server, so everything below is delivered to your
                Telegram as it happens — and recorded here either way.
              </>
            ) : (
              <>
                No bot token or chat id on the server, so nothing reaches Telegram. Events are still
                recorded in the stream below. Set <code>Telegram:BotToken</code> and{' '}
                <code>Telegram:ChatId</code> in the API configuration to turn delivery on.
              </>
            )}
          </p>
        </div>
      </div>

      <div className="tablewrap" style={{ marginTop: 12 }}>
        <table className="table">
          <thead>
            <tr>
              <th>Sent automatically</th>
              <th>What the message carries</th>
            </tr>
          </thead>
          <tbody>
            {WHAT_GETS_SENT.map((row) => (
              <tr key={row.label}>
                <td>{row.label}</td>
                <td className="muted">{row.detail}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="small-note muted">
        Delivery never blocks the thing it reports on: if Telegram or Redis is down, the run still
        starts and the failure is logged rather than raised.
      </p>
    </Panel>
  )
}

function AlerterPanel({ status }: { status: AlerterStatus }) {
  const qc = useQueryClient()
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)

  const control = useMutation({
    mutationFn: (action: 'start' | 'stop') =>
      api.post<{ message: string }>(`/api/Alerts/${action}`),
    onSuccess: (res) => {
      setNotice({ ok: true, text: res.message })
      qc.invalidateQueries({ queryKey: ['alerts'] })
    },
    onError: (err: unknown) => {
      const message =
        (err as { body?: { message?: string }; message?: string })?.body?.message ??
        (err as { message?: string })?.message ??
        'Failed'
      setNotice({ ok: false, text: message })
    },
  })

  const test = useMutation({
    mutationFn: (instrument: string) =>
      api.post<{ message: string }>('/api/Alerts/test-e2e', { instrument }),
    onSuccess: (res) => {
      setNotice({ ok: true, text: res.message })
      // The alert travels Redis → subscriber → database; give it a moment.
      setTimeout(() => qc.invalidateQueries({ queryKey: ['alerts', 'events'] }), 1500)
    },
    onError: (err: unknown) => {
      const message =
        (err as { body?: { message?: string }; message?: string })?.body?.message ??
        (err as { message?: string })?.message ??
        'Failed'
      setNotice({ ok: false, text: message })
    },
  })

  return (
    <Panel
      title={
        <span className="chip-row">
          Signal alerter
          <Badge tone={status.isRunning ? 'pos' : 'warn'}>
            {status.isRunning ? 'running' : 'stopped'}
          </Badge>
        </span>
      }
      actions={
        <div className="chip-row">
          <button
            type="button"
            className="btn btn--primary btn--sm"
            disabled={control.isPending || status.isRunning}
            onClick={() => control.mutate('start')}
          >
            Start
          </button>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={control.isPending || !status.isRunning}
            onClick={() => control.mutate('stop')}
          >
            Stop
          </button>
        </div>
      }
    >
      <p className="muted" style={{ maxWidth: '78ch' }}>
        The Python process that watches the market and raises strategy signals. It is separate from the
        automatic events above, which the platform sends whether or not this is running.
      </p>

      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Underlying</th>
              <th>Process</th>
              <th>Owner</th>
            </tr>
          </thead>
          <tbody>
            {status.processes.length === 0 ? (
              <tr>
                <td colSpan={3} className="muted">
                  No alerter targets configured.
                </td>
              </tr>
            ) : (
              status.processes.map((p) => (
                <tr key={p.underlying}>
                  <td>{p.underlying}</td>
                  <td className="mono">{p.processId ?? <span className="muted">—</span>}</td>
                  <td>
                    {p.source === 'none' ? (
                      <Badge tone="warn">not running</Badge>
                    ) : (
                      <Badge tone="pos">{p.source}</Badge>
                    )}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      <h3 className="section-title connector-section">End-to-end test</h3>
      <p className="muted" style={{ maxWidth: '78ch' }}>
        Sends a mock signal along the whole path — Redis → the API subscriber → Telegram and the
        database — so a silent pipeline is caught here rather than during a trade.
      </p>
      <div className="chip-row">
        <button
          type="button"
          className="btn btn--ghost btn--sm"
          disabled={test.isPending}
          onClick={() => test.mutate('BANKNIFTY')}
        >
          {test.isPending ? 'Sending…' : 'Test with BANKNIFTY'}
        </button>
        <button
          type="button"
          className="btn btn--ghost btn--sm"
          disabled={test.isPending}
          onClick={() => test.mutate('RELIANCE')}
        >
          Test with RELIANCE
        </button>
      </div>

      {notice && (
        <div className={`alert ${notice.ok ? 'alert--success' : 'alert--error'}`} role="status">
          {notice.text}
        </div>
      )}
    </Panel>
  )
}

export function LiveAlertsV2Page() {
  const status = useAlerterStatus()
  const events = useAlertEvents(100)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Alerts</h1>
        <p className="page__subtitle">
          One channel for everything the platform needs to tell you — run starts and stops, the kill
          switch, and strategy signals — delivered to Telegram and recorded here.
        </p>
      </header>

      <QueryBoundary query={status}>
        {(s) => (
          <>
            <DeliveryPanel status={s} />
            <AlerterPanel status={s} />
          </>
        )}
      </QueryBoundary>

      <Panel
        title="Event stream"
        actions={
          <span className="muted small-note">
            {events.data?.length ?? 0} most recent
          </span>
        }
      >
        {events.isError && <InlineError error={events.error} />}
        {events.data && events.data.length === 0 ? (
          <EmptyState>
            Nothing yet. Start a strategy run, toggle the kill switch, or use the end-to-end test above
            and it will appear here.
          </EmptyState>
        ) : (
          <div className="tablewrap tablewrap--tall">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Severity</th>
                  <th>Event</th>
                  <th>Source</th>
                  <th>Telegram</th>
                </tr>
              </thead>
              <tbody>
                {(events.data ?? []).map((ev) => (
                  <tr key={ev.id}>
                    <td className="mono">{formatDateTime(ev.occurredUtc)}</td>
                    <td>
                      <Badge tone={severityTone(ev.severity)}>{ev.severity}</Badge>
                    </td>
                    <td>
                      <div>{ev.title}</div>
                      <div className="small-note muted" style={{ whiteSpace: 'pre-line' }}>
                        {ev.message}
                      </div>
                      {(ev.symbol || ev.simulationRunId) && (
                        <div className="small-note muted mono">
                          {ev.symbol}
                          {ev.symbol && ev.simulationRunId ? ' · ' : ''}
                          {ev.simulationRunId ? `run #${ev.simulationRunId}` : ''}
                        </div>
                      )}
                    </td>
                    <td className="mono">
                      {ev.source}
                      {ev.underlying && ev.underlying !== 'UNKNOWN' && (
                        <div className="small-note muted">{ev.underlying}</div>
                      )}
                    </td>
                    <td>
                      {ev.deliveredToTelegram ? (
                        <Badge tone="pos">delivered</Badge>
                      ) : (
                        <Badge tone="neutral">recorded only</Badge>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  )
}
