/**
 * TrueData controls, each placed where its kind of thing already lives.
 *
 * The feed is a feed, so it sits on Live feeds beside the FYERS one. Its symbol
 * names are part of the symbol masters, so the import sits with the other
 * masters. Neither belongs on the Connectors page, which configures a connector
 * and shows no market data.
 */

import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { InlineError, Panel } from '../../components/ui'

interface FeedStatus {
  isRunning: boolean
  managed: boolean
  processId: number | null
  source: string
}

/** The TrueData live feed: status, start and stop. Runs beside the FYERS feed. */
export function TrueDataFeedPanel() {
  const qc = useQueryClient()
  const status = useQuery({
    queryKey: ['truedata', 'feed'],
    queryFn: () => api.get<FeedStatus>('/api/TrueData/feed/status'),
    refetchInterval: 10_000,
  })

  const start = useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/TrueData/feed/start'),
    onSettled: () => qc.invalidateQueries({ queryKey: ['truedata', 'feed'] }),
  })
  const stop = useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/TrueData/feed/stop'),
    onSettled: () => qc.invalidateQueries({ queryKey: ['truedata', 'feed'] }),
  })

  const running = status.data?.isRunning ?? false

  return (
    <Panel
      title="TrueData feed"
      actions={
        // Nothing is offered until the state is known: Start on a feed that is
        // already running is the one click here that does harm.
        status.isPending ? (
          <button className="btn btn--sm" disabled>Checking…</button>
        ) : running ? (
          <button className="btn btn--danger btn--sm" disabled={stop.isPending} onClick={() => stop.mutate()}>
            {stop.isPending ? 'Stopping…' : 'Stop'}
          </button>
        ) : (
          <button className="btn btn--pos btn--sm" disabled={start.isPending} onClick={() => start.mutate()}>
            {start.isPending ? 'Starting…' : 'Start'}
          </button>
        )
      }
    >
      <p style={{ margin: 0 }}>
        <span className={status.isPending ? 'muted' : running ? 'pos' : 'muted'}>
          {status.isPending
            ? 'checking…'
            : status.isError
              ? 'status unavailable'
              : running
                ? `running${status.data?.processId ? ` · pid ${status.data.processId}` : ''}`
                : 'stopped'}
        </span>
      </p>
      <p className="muted" style={{ maxWidth: '80ch', marginBottom: 0 }}>
        A second live feed beside FYERS, stamped <code className="mono">truedata</code> on every row so the two
        can be compared. It carries open interest, which FYERS does not. The trial allows 50 symbols and the
        recording list has more; the ones left out are named in the diagnostics log. One session per account,
        so stop any other TrueData client first.
      </p>
      {start.isError && <InlineError error={start.error} />}
      {stop.isError && <InlineError error={stop.error} />}
    </Panel>
  )
}

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

/** TrueData's names for derivatives, imported beside the FYERS symbol masters. */
export function TrueDataSymbolImport() {
  const [result, setResult] = useState<ImportResult | null>(null)
  const run = useMutation({
    mutationFn: () => api.post<ImportResult>('/api/TrueData/symbols/import'),
    onSuccess: setResult,
  })

  return (
    <div style={{ marginTop: 14 }}>
      <div className="inline-form" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
        <b style={{ fontSize: 13 }}>TrueData names</b>
        <span className="muted" style={{ fontSize: 12.5 }}>
          futures and monthly options, which cannot be worked out from our own symbol
        </span>
        <button className="btn btn--sm" disabled={run.isPending} onClick={() => run.mutate()}>
          {run.isPending ? 'Importing…' : 'Import'}
        </button>
      </div>
      {run.isError && <InlineError error={run.error} />}
      {result && (
        <p className="small-note" style={{ marginTop: 6 }}>
          {result.mapped.toLocaleString('en-IN')} mapped from {result.rowsRead.toLocaleString('en-IN')} rows
          {' · '}
          {result.segments.map((s) => `${s.segment} ${s.error ? 'failed' : s.mapped.toLocaleString('en-IN')}`).join(', ')}
        </p>
      )}
    </div>
  )
}
