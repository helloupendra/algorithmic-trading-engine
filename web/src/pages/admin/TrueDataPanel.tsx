/**
 * TrueData's symbol-name import, placed with the other symbol masters because
 * that is the kind of thing it is. It does not belong on the Connectors page,
 * which configures a connector and shows no market data.
 *
 * TrueData's live feed is not here: it is a row in the Live feeds page's
 * FeedsPanel, like every other vendor's.
 */

import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { InlineError } from '../../components/ui'

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
