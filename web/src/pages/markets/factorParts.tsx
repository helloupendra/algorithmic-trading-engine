/**
 * The small pieces every market-factor reading shares, wherever it now lives:
 * the Factors page (flows, global, events), the chain's Levels view and the
 * futures build-up on Movers. Each says what our own tests found for that
 * factor, and each dataset says how fresh it is, so a reading is never taken
 * for a proven signal.
 */

import type { ReactNode } from 'react'
import { useSyncMarketFactors } from '../../lib/queries'
import { DATASET_LABELS, RESEARCH_NOTES, formatDay } from '../../lib/factors'
import type { DatasetStatus } from '../../lib/factors'
import { formatAge } from '../../lib/format'
import { useAuth } from '../../lib/auth'
import { Badge, InlineError } from '../../components/ui'
import '../data/factors.css'

/** A number in Indian grouping, or a dash when there is none. */
export const num = (v: number | null | undefined, digits = 0) =>
  v == null || Number.isNaN(v) ? '—' : v.toLocaleString('en-IN', { minimumFractionDigits: digits, maximumFractionDigits: digits })

export function ResearchNote({ id }: { id: keyof typeof RESEARCH_NOTES }) {
  const note = RESEARCH_NOTES[id]
  return (
    <p className="mf-research small">
      <Badge tone={note.tested ? 'accent' : 'neutral'}>{note.tested ? 'Tested' : 'Not tested yet'}</Badge> {note.text}
    </p>
  )
}

export function Metric({ label, value, sub, tone }: { label: string; value: ReactNode; sub?: ReactNode; tone?: string }) {
  return (
    <div className="mf-metric">
      <div className="mf-metric__label">{label}</div>
      <div className={`mf-metric__value mono ${tone ?? ''}`}>{value}</div>
      {sub != null && <div className="mf-metric__sub small muted">{sub}</div>}
    </div>
  )
}

/** When each of NSE's evening files was last fetched, and (for an admin) a way to fetch them now. */
export function StatusLine({ status, datasets }: { status: DatasetStatus[] | undefined; datasets: string[] }) {
  const { user } = useAuth()
  const sync = useSyncMarketFactors()
  const rows = datasets.map((d) => status?.find((s) => s.dataset === d) ?? null)

  return (
    <div className="mf-status small">
      {datasets.map((d, i) => {
        const s = rows[i]
        return (
          <span key={d} className="mf-status__item">
            <span className={`live-dot ${s?.lastFailed ? 'neg' : s?.lastSuccessUtc ? 'pos' : ''}`} aria-hidden />
            {DATASET_LABELS[d] ?? d}:{' '}
            {s
              ? `${s.newestDay ? `newest ${formatDay(s.newestDay)}` : 'nothing stored yet'} · checked ${formatAge(s.lastAttemptUtc)}${
                  s.lastFailed && s.lastMessage ? ` · ${s.lastMessage}` : ''
                }`
              : 'not fetched since the API started'}
          </span>
        )
      })}
      {user?.role === 'Admin' && (
        <button type="button" className="btn btn--sm" onClick={() => sync.mutate()} disabled={sync.isPending}>
          {sync.isPending ? 'Fetching from NSE…' : 'Fetch now'}
        </button>
      )}
      {sync.isError && <InlineError error={sync.error} />}
    </div>
  )
}
