/**
 * System module — Deployments: what this machine did with the last pushes.
 *
 * A push from anywhere lands here on its own (scripts/auto-deploy.ps1, on a
 * schedule), and "is my change live?" then has four separate answers: did it
 * pull, did the console rebuild, did the backend restart, and was anything left
 * for a human. The Telegram message says all four in prose; this says them as
 * state, with the time against each, which is what you want when something has
 * gone wrong rather than right.
 *
 * Read-only. The deploy is performed by the script; this page never triggers one.
 */

import { useQuery } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'

interface DeployStep {
  name: string
  status: 'ok' | 'skipped' | 'failed'
  detail: string
  atUtc: string
}

interface DeployRecord {
  startedUtc: string
  finishedUtc: string
  outcome: 'applied' | 'skipped' | 'failed'
  summary: string
  fromCommit: string
  toCommit: string
  commits: string[]
  filesChanged: number
  steps: DeployStep[]
  machine: string
}

interface DeployHistory {
  file: string | null
  entries: DeployRecord[]
  unreadable?: boolean
}

function useDeployHistory() {
  return useQuery({
    queryKey: ['deploy', 'history'],
    queryFn: () => api.get<DeployHistory>('/api/Deploy/history?limit=20'),
    // Often enough that watching a deploy land feels live, rare enough that an
    // idle console is not asking a question nobody posed.
    refetchInterval: 10_000,
  })
}

const OUTCOME_TONE = {
  applied: 'pos',
  skipped: 'warn',
  failed: 'neg',
} as const

const OUTCOME_LABEL = {
  applied: 'Live',
  skipped: 'Skipped',
  failed: 'Needs you',
} as const

const STEP_MARK = {
  ok: '✓',
  skipped: '–',
  failed: '✕',
} as const

function StepRow({ step }: { step: DeployStep }) {
  const tone = step.status === 'ok' ? 'pos' : step.status === 'failed' ? 'neg' : 'muted'
  return (
    <div className="deploy-step">
      <span className={`deploy-step__mark ${tone}`} aria-hidden="true">
        {STEP_MARK[step.status]}
      </span>
      <span className="deploy-step__name">{step.name}</span>
      <span className="deploy-step__detail muted">{step.detail}</span>
      <span className="deploy-step__time faint" title={formatDateTime(step.atUtc)}>
        {new Date(step.atUtc).toLocaleTimeString('en-IN')}
      </span>
    </div>
  )
}

function DeployCard({ record }: { record: DeployRecord }) {
  const took = Math.max(
    0,
    (new Date(record.finishedUtc).getTime() - new Date(record.startedUtc).getTime()) / 1000,
  )

  return (
    <div className="deploy-card">
      <div className="deploy-card__head">
        <Badge tone={OUTCOME_TONE[record.outcome]}>{OUTCOME_LABEL[record.outcome]}</Badge>
        <span className="deploy-card__summary">{record.summary}</span>
        <span className="deploy-card__when faint" title={formatDateTime(record.finishedUtc)}>
          {formatAge(record.finishedUtc)}
        </span>
      </div>

      <div className="deploy-card__meta muted">
        {record.fromCommit && record.toCommit ? (
          <span className="mono">
            {record.fromCommit} → {record.toCommit}
          </span>
        ) : record.toCommit ? (
          <span className="mono">origin at {record.toCommit}</span>
        ) : null}
        {record.filesChanged > 0 && <span>· {record.filesChanged} file(s)</span>}
        <span>· took {took < 1 ? '<1' : took.toFixed(0)}s</span>
        <span>· {record.machine}</span>
      </div>

      {record.commits?.length > 0 && (
        <ul className="deploy-card__commits mono">
          {record.commits.slice(0, 6).map((c, i) => (
            <li key={i}>{c}</li>
          ))}
          {record.commits.length > 6 && (
            <li className="faint">…and {record.commits.length - 6} more</li>
          )}
        </ul>
      )}

      <div className="deploy-card__steps">
        {record.steps?.length ? (
          record.steps.map((s, i) => <StepRow key={i} step={s} />)
        ) : (
          <span className="faint">No steps recorded.</span>
        )}
      </div>
    </div>
  )
}

export function DeploymentsPage() {
  const history = useDeployHistory()
  const entries = history.data?.entries ?? []
  const latest = entries[0]

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Deployments</h1>
          <p className="page__subtitle">
            What this machine did with the last pushes. A push from anywhere is pulled here
            automatically; the console rebuilds without a restart, and the backend restarts only
            when nothing is trading.
          </p>
        </div>
      </header>

      {latest && (
        <div className="stat-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}>
          <div className="stat">
            <div className="stat__value">
              <span className={OUTCOME_TONE[latest.outcome] === 'pos' ? 'pos' : OUTCOME_TONE[latest.outcome] === 'neg' ? 'neg' : ''}>
                {OUTCOME_LABEL[latest.outcome]}
              </span>
            </div>
            <div className="stat__label">Last deploy</div>
            <div className="stat__sub">{latest.summary}</div>
          </div>
          <div className="stat">
            <div className="stat__value">{formatAge(latest.finishedUtc)}</div>
            <div className="stat__label">When</div>
            <div className="stat__sub">{formatDateTime(latest.finishedUtc)}</div>
          </div>
          <div className="stat">
            <div className="stat__value mono" style={{ fontSize: 18 }}>
              {latest.toCommit || '—'}
            </div>
            <div className="stat__label">Running commit</div>
            <div className="stat__sub">{latest.filesChanged} file(s) in that pull</div>
          </div>
        </div>
      )}

      <Panel
        title="History"
        actions={
          <span className="faint" style={{ fontSize: 12 }}>
            Newest first · refreshes every 10s
          </span>
        }
      >
        {history.isLoading ? (
          <Loading label="Reading the deploy record…" />
        ) : history.error ? (
          <InlineError error={history.error} />
        ) : entries.length === 0 ? (
          <EmptyState>
            {history.data?.unreadable
              ? 'The record was being rewritten as this page read it - it will appear on the next refresh.'
              : 'Nothing deployed yet. This machine has not taken a push since the watcher was installed; push to main and it will appear here within a couple of minutes.'}
          </EmptyState>
        ) : (
          <div className="deploy-list">
            {entries.map((r, i) => (
              <DeployCard key={`${r.finishedUtc}-${i}`} record={r} />
            ))}
          </div>
        )}
      </Panel>
    </div>
  )
}
