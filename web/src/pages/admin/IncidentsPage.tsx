/**
 * System → Incidents: what Sentinel, the desk's own watchman, has found.
 *
 * Sentinel runs four agents against the live desk — health, trading, logs and
 * security — and records each problem once, however many checks see it again.
 * This page is where those problems are read, acknowledged ("someone is on it")
 * and resolved. The buttons change the incident's own status and nothing else:
 * Sentinel never touches production, and neither does this page.
 *
 * Admin-only, like the activity log: evidence names runs, accounts and files.
 */

import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import {
  useAcknowledgeIncident,
  useIncidentSummary,
  useIncidents,
  useResolveIncident,
} from '../../lib/queries'
import type { Incident, IncidentSummary } from '../../lib/types'
import {
  INCIDENT_AGENTS,
  INCIDENT_VIEWS,
  SEVERITY_LABEL,
  SEVERITY_ORDER,
  STATUS_TONE,
  countFor,
  countTone,
  emptyMessage,
  listState,
  maskSecrets,
  occurrencesText,
  resolveConfirmText,
  severityTone,
  sortNewestFirst,
  watchmanNote,
} from '../../lib/incidents'
import type { IncidentView, WatchmanNote } from '../../lib/incidents'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import './incidents.css'

const TAKE = 200
const COLUMNS = 6

/** Re-renders on a clock, so "3m ago" keeps moving between polls that change nothing. */
function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}

function agentLabel(key: string): string {
  return INCIDENT_AGENTS.find((a) => a.key === key)?.label ?? key
}

function SeverityBadge({ severity }: { severity: string }) {
  const label = SEVERITY_LABEL[severity as keyof typeof SEVERITY_LABEL] ?? severity
  const critical = severity === 'critical' ? ' incident-sev--critical' : ''
  return <span className={`badge badge--${severityTone(severity)}${critical}`}>{label}</span>
}

function StatusBadge({ incident }: { incident: Incident }) {
  return <Badge tone={STATUS_TONE[incident.status] ?? 'neutral'}>{incident.status}</Badge>
}

/** Whether Sentinel is still looking — said above the counts, because it decides what they mean. */
function WatchmanLine({ note }: { note: WatchmanNote | null }) {
  if (!note) return null
  if (note.tone === 'warn') {
    return (
      <div className="alert alert--warn" role="status">
        {note.text}
      </div>
    )
  }
  return (
    <p className="small-note muted" role="status" style={{ margin: 0 }}>
      {note.text}
    </p>
  )
}

function SummaryTiles({ summary }: { summary: IncidentSummary }) {
  return (
    <>
      <div className="stat-grid">
        {SEVERITY_ORDER.map((sev) => {
          const n = countFor(summary, sev)
          return (
            <StatTile
              key={sev}
              label={SEVERITY_LABEL[sev]}
              value={n == null ? '—' : n}
              tone={countTone(sev, n)}
            />
          )
        })}
        <StatTile
          label="Newest"
          value={summary.newestUtc ? formatAge(summary.newestUtc) : '—'}
          sub={summary.newestTitle ? maskSecrets(summary.newestTitle) : 'No live incident'}
        />
      </div>
    </>
  )
}

function IncidentRow({ incident }: { incident: Incident }) {
  const [open, setOpen] = useState(false)
  const acknowledge = useAcknowledgeIncident()
  const resolve = useResolveIncident()
  const busy = acknowledge.isPending || resolve.isPending
  const actionError = acknowledge.error ?? resolve.error
  const title = maskSecrets(incident.title)

  function onResolve() {
    // Closing a record whose problem is still there buys a new incident and a
    // new alert on the next check; say so before, not after.
    if (window.confirm(resolveConfirmText({ id: incident.id, title }))) resolve.mutate(incident.id)
  }

  return (
    <>
      <tr
        className={open ? 'incident-row--open' : undefined}
        onClick={() => setOpen((o) => !o)}
        style={{ cursor: 'pointer' }}
      >
        <td>
          <SeverityBadge severity={incident.severity} />
        </td>
        <td>
          {/* No handler of its own: Enter/Space fire a click that bubbles to the row. */}
          <button type="button" className="incident-toggle" aria-expanded={open}>
            <span className="incident-toggle__caret" aria-hidden="true">
              {open ? '▾' : '▸'}
            </span>
            <span>{title}</span>
          </button>
        </td>
        <td>
          {agentLabel(incident.agent)}
          <div className="small-note muted mono" style={{ marginTop: 2 }}>
            {incident.rule}
          </div>
        </td>
        <td>
          {incident.location ? (
            <span className="mono incident-where" title={maskSecrets(incident.location)}>
              {maskSecrets(incident.location)}
            </span>
          ) : (
            <span className="faint">—</span>
          )}
        </td>
        <td
          className="mono incident-seen"
          title={`first ${formatDateTime(incident.firstSeenUtc)} · last ${formatDateTime(incident.lastSeenUtc)} IST`}
        >
          {incident.occurrences}× · {formatAge(incident.lastSeenUtc)}
        </td>
        <td className="incident-status">
          <StatusBadge incident={incident} />
          {incident.status === 'acknowledged' && incident.acknowledgedBy && (
            <div className="small-note muted" style={{ marginTop: 2 }}>
              by {incident.acknowledgedBy}
            </div>
          )}
        </td>
      </tr>

      {open && (
        <tr className="incident-detail">
          <td colSpan={COLUMNS}>
            <div className="incident-detail__body">
              <p className="incident-detail__summary">{maskSecrets(incident.summary)}</p>

              {incident.evidence?.length > 0 && (
                <div>
                  <h3 className="section-title incident-detail__label">What Sentinel saw</h3>
                  <ul className="incident-evidence">
                    {incident.evidence.map((line, i) => (
                      <li key={i}>{maskSecrets(line)}</li>
                    ))}
                  </ul>
                </div>
              )}

              {incident.suggestion && (
                <div>
                  <h3 className="section-title incident-detail__label">First thing to do</h3>
                  <p className="incident-detail__suggestion">{maskSecrets(incident.suggestion)}</p>
                </div>
              )}

              <div className="incident-detail__meta">
                Seen {occurrencesText(incident.occurrences)} · first {formatDateTime(incident.firstSeenUtc)} IST ·
                last {formatDateTime(incident.lastSeenUtc)} IST
                {incident.acknowledgedUtc && (
                  <>
                    {' '}· acknowledged {formatAge(incident.acknowledgedUtc)}
                    {incident.acknowledgedBy ? ` by ${incident.acknowledgedBy}` : ''}
                  </>
                )}
                {incident.resolvedUtc && (
                  <>
                    {' '}· resolved {formatDateTime(incident.resolvedUtc)} IST
                    {incident.resolvedBy ? ` by ${incident.resolvedBy}` : ''}
                  </>
                )}
                <div className="mono" style={{ marginTop: 3 }}>
                  #{incident.id} · {incident.fingerprint}
                </div>
              </div>

              {incident.status !== 'resolved' && (
                <div className="incident-detail__actions">
                  {incident.status === 'open' && (
                    <button
                      type="button"
                      className="btn btn--sm"
                      disabled={busy}
                      onClick={() => acknowledge.mutate(incident.id)}
                    >
                      {acknowledge.isPending ? 'Acknowledging…' : 'Acknowledge'}
                    </button>
                  )}
                  <button
                    type="button"
                    className="btn btn--sm btn--primary"
                    disabled={busy}
                    onClick={onResolve}
                  >
                    {resolve.isPending ? 'Resolving…' : 'Resolve'}
                  </button>
                  <p className="small-note muted">
                    Resolve only closes the record. If the problem is still there, Sentinel opens a new
                    incident on its next check and alerts again.
                  </p>
                </div>
              )}

              {actionError && <InlineError error={actionError} />}
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

function IncidentTable({ incidents }: { incidents: Incident[] }) {
  return (
    <div className="tablewrap tablewrap--tall">
      <table className="table">
        <thead>
          <tr>
            <th>Severity</th>
            <th>What is wrong</th>
            <th>Agent · rule</th>
            <th>Where</th>
            <th>Seen · last</th>
            <th className="incident-status">Status</th>
          </tr>
        </thead>
        <tbody>
          {incidents.map((incident) => (
            <IncidentRow key={incident.id} incident={incident} />
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function IncidentsPage() {
  const [view, setView] = useState<IncidentView>('live')
  const [agent, setAgent] = useState('')
  // Only to keep "3m ago" moving between polls that change nothing.
  useNow(15_000)

  const summary = useIncidentSummary()
  const list = useIncidents({ status: view, agent: agent || undefined, take: TAKE })
  // Every live incident, whatever the filters: whether Sentinel is still
  // re-seeing them is the page's only evidence that it is running. The same
  // query as the default view, so on "Live · All agents" it is one request.
  const live = useIncidents({ status: 'live', take: TAKE })

  const rows = list.data ? sortNewestFirst(list.data) : undefined
  const viewLabel = INCIDENT_VIEWS.find((v) => v.key === view)?.label ?? view

  // Judged at the moment each answer arrived, not by the wall clock: a tab
  // woken from sleep must not flash "stopped" before its refetch lands.
  const watchman: WatchmanNote | null = watchmanNote({
    lastCheckUtc: summary.data?.lastCheckUtc,
    asOfMs: summary.dataUpdatedAt,
    liveRows: live.isError ? undefined : live.data,
    liveAsOfMs: live.dataUpdatedAt,
  })

  let body: ReactNode
  const state = listState(list)
  if (state === 'loading') {
    body = <Loading label="Reading Sentinel's incidents…" />
  } else if (state === 'error' || !rows) {
    // An empty list from a failed refresh is not "nothing open": say what failed.
    body = <InlineError error={list.error} />
  } else if (state === 'empty') {
    body = <EmptyState>{emptyMessage(view, agent || undefined)}</EmptyState>
  } else {
    body = (
      <>
        {list.isError && (
          <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
            Refresh failed — showing the list as it was {formatAge(new Date(list.dataUpdatedAt).toISOString())}.
          </p>
        )}
        <IncidentTable incidents={rows} />
        {rows.length >= TAKE && (
          <p className="small-note muted">Showing the newest {TAKE}. Narrow by agent to see further back.</p>
        )}
      </>
    )
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Incidents</h1>
          <p className="page__subtitle">
            What Sentinel has found on this desk. Each problem is one row however often it is seen;
            it resolves itself after enough clean checks. Sentinel only watches — nothing here changes
            production.
          </p>
        </div>
      </header>

      {summary.isPending ? (
        <Loading label="Counting live incidents…" />
      ) : summary.data ? (
        <>
          {summary.isError && (
            <p className="small-note warn" role="status" style={{ margin: 0 }}>
              The counts did not refresh — these are from {formatAge(new Date(summary.dataUpdatedAt).toISOString())}.
            </p>
          )}
          <WatchmanLine note={watchman} />
          <SummaryTiles summary={summary.data} />
        </>
      ) : (
        <InlineError error={summary.error} />
      )}

      <Panel
        title={
          <span className="chip-row">
            {view === 'live' ? 'Live — open or acknowledged' : viewLabel}
            {agent && <Badge tone="accent">{agentLabel(agent)}</Badge>}
            {rows && rows.length > 0 && <span className="faint">{rows.length}</span>}
          </span>
        }
        actions={
          <div className="chip-row">
            <div className="seg" role="group" aria-label="Status">
              {INCIDENT_VIEWS.map((v) => (
                <button
                  key={v.key}
                  type="button"
                  className={`seg__btn ${view === v.key ? 'is-active' : ''}`}
                  aria-pressed={view === v.key}
                  onClick={() => setView(v.key)}
                >
                  {v.label}
                </button>
              ))}
            </div>
            <div className="seg" role="group" aria-label="Agent">
              <button
                type="button"
                className={`seg__btn ${agent === '' ? 'is-active' : ''}`}
                aria-pressed={agent === ''}
                onClick={() => setAgent('')}
              >
                All agents
              </button>
              {INCIDENT_AGENTS.map((a) => (
                <button
                  key={a.key}
                  type="button"
                  className={`seg__btn ${agent === a.key ? 'is-active' : ''}`}
                  aria-pressed={agent === a.key}
                  onClick={() => setAgent(a.key)}
                >
                  {a.label}
                </button>
              ))}
            </div>
          </div>
        }
      >
        {body}
        <p className="small-note muted">
          Newest first · refreshes every 15 s. Acknowledge says someone is on it and keeps it live;
          Resolve closes it. Times are IST.
        </p>
      </Panel>
    </div>
  )
}
