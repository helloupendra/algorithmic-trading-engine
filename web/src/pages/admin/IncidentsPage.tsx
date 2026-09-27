/**
 * System → Incidents: what Sentinel, the desk's own watchman, has found.
 *
 * Sentinel runs four agents against the live desk — health, trading, logs and
 * security — and records each problem once, however many checks see it again.
 * This page is where those problems are read, acknowledged ("someone is on it")
 * and resolved. The buttons change the incident's own status and notes and
 * nothing else: Sentinel never touches production, and neither does this page.
 *
 * It is also the desk's memory of its own failures. Each incident says whether
 * the problem has happened before and what was done the last time; resolving
 * asks what caused it and what fixed it; and the History tab lays every
 * problem out by how often it came back, how long it took to clear, and what
 * was learnt — so "has this happened before?" is answered from the record.
 *
 * Admin-only, like the activity log: evidence names runs, accounts and files.
 */

import { useEffect, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import {
  useAcknowledgeIncident,
  useIncidentHistory,
  useIncidentNotes,
  useIncidentSummary,
  useIncidents,
  useResolveIncident,
} from '../../lib/queries'
import type {
  Incident,
  IncidentEpisode,
  IncidentHistoryRow,
  IncidentNotes,
  IncidentSummary,
} from '../../lib/types'
import {
  FIX_REF_MAX_CHARS,
  HISTORY_WINDOWS,
  INCIDENT_AGENTS,
  INCIDENT_VIEWS,
  NOTE_MAX_CHARS,
  RESOLVE_WARNING,
  SEVERITY_LABEL,
  SEVERITY_ORDER,
  STATUS_TONE,
  countFor,
  countTone,
  dayText,
  emptyMessage,
  endedText,
  episodesText,
  fixRefLink,
  hasNotes,
  lastedText,
  listState,
  maskSecrets,
  mttrText,
  notesBody,
  notesFormFrom,
  occurrencesText,
  seenBeforeText,
  severityTone,
  sortNewestFirst,
  watchmanNote,
} from '../../lib/incidents'
import type { IncidentView, NotesForm, WatchmanNote } from '../../lib/incidents'
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

/**
 * Root cause, what was done, where the fix is. The same three fields on
 * Resolve and on "Edit notes", started from what is already written so a
 * resolve never wipes notes made while the incident was open.
 */
function NotesFormFields({
  id,
  initial,
  submitLabel,
  pendingLabel,
  pending,
  note,
  onSubmit,
  onCancel,
}: {
  id: number
  initial: NotesForm
  submitLabel: string
  pendingLabel: string
  pending: boolean
  note?: ReactNode
  onSubmit: (notes: IncidentNotes) => void
  onCancel: () => void
}) {
  const [form, setForm] = useState<NotesForm>(initial)
  const set = (key: keyof NotesForm) => (e: { target: { value: string } }) =>
    setForm((f) => ({ ...f, [key]: e.target.value }))

  function submit(e: FormEvent) {
    e.preventDefault()
    onSubmit(notesBody(form))
  }

  return (
    <form className="incident-notes-form" onSubmit={submit}>
      <label className="field" htmlFor={`incident-${id}-cause`}>
        <span className="field__label">Root cause</span>
        <textarea
          id={`incident-${id}-cause`}
          className="field__input"
          rows={2}
          maxLength={NOTE_MAX_CHARS}
          value={form.rootCause}
          onChange={set('rootCause')}
          placeholder="Why it happened, if known"
        />
      </label>
      <label className="field" htmlFor={`incident-${id}-done`}>
        <span className="field__label">What was done</span>
        <textarea
          id={`incident-${id}-done`}
          className="field__input"
          rows={2}
          maxLength={NOTE_MAX_CHARS}
          value={form.resolution}
          onChange={set('resolution')}
          placeholder="What fixed it, or what to do next time"
        />
      </label>
      <label className="field" htmlFor={`incident-${id}-ref`}>
        <span className="field__label">Fix reference</span>
        <input
          id={`incident-${id}-ref`}
          className="field__input mono"
          maxLength={FIX_REF_MAX_CHARS}
          value={form.fixRef}
          onChange={set('fixRef')}
          placeholder="Commit sha, pull request or doc link"
          autoComplete="off"
          spellCheck={false}
        />
      </label>
      {note && <p className="small-note muted">{note}</p>}
      <div className="incident-detail__actions">
        <button type="submit" className="btn btn--sm btn--primary" disabled={pending}>
          {pending ? pendingLabel : submitLabel}
        </button>
        <button type="button" className="btn btn--sm" disabled={pending} onClick={onCancel}>
          Cancel
        </button>
        <span className="small-note muted">All optional. Anything that looks like a secret is masked when saved.</span>
      </div>
    </form>
  )
}

/** The fix reference: a link when it is an http(s) URL, otherwise text. */
function FixRef({ value }: { value: string }) {
  const href = fixRefLink(value)
  const text = maskSecrets(value)
  return href ? (
    <a className="mono incident-fixref" href={href} target="_blank" rel="noreferrer noopener">
      {text}
    </a>
  ) : (
    <span className="mono incident-fixref">{text}</span>
  )
}

/** What was learnt from one episode, or nothing when nobody wrote anything. */
function NotesList({ notes }: { notes: { rootCause?: string | null; resolution?: string | null; fixRef?: string | null } }) {
  if (!hasNotes(notes)) return null
  return (
    <dl className="incident-notes">
      {notes.rootCause && (
        <>
          <dt>Root cause</dt>
          <dd>{maskSecrets(notes.rootCause)}</dd>
        </>
      )}
      {notes.resolution && (
        <>
          <dt>What was done</dt>
          <dd>{maskSecrets(notes.resolution)}</dd>
        </>
      )}
      {notes.fixRef && (
        <>
          <dt>Fix</dt>
          <dd>
            <FixRef value={notes.fixRef} />
          </dd>
        </>
      )}
    </dl>
  )
}

function IncidentRow({ incident }: { incident: Incident }) {
  const [open, setOpen] = useState(false)
  // Resolve and "Edit notes" open the notes form in place of the buttons.
  const [editing, setEditing] = useState<'resolve' | 'notes' | null>(null)
  const acknowledge = useAcknowledgeIncident()
  const resolve = useResolveIncident()
  const saveNotes = useIncidentNotes()
  const busy = acknowledge.isPending || resolve.isPending || saveNotes.isPending
  const actionError = acknowledge.error ?? resolve.error ?? saveNotes.error
  const title = maskSecrets(incident.title)
  const seenBefore = seenBeforeText(incident)
  const recurring = (incident.previousEpisodes ?? 0) > 0

  function onResolve(notes: IncidentNotes) {
    resolve.mutate({ id: incident.id, notes }, { onSuccess: () => setEditing(null) })
  }

  function onSaveNotes(notes: IncidentNotes) {
    saveNotes.mutate({ id: incident.id, notes }, { onSuccess: () => setEditing(null) })
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
            <span>
              {title}
              {recurring && (
                <span className="incident-again" title={seenBefore ?? undefined}>
                  {' '}
                  · seen {occurrencesText(incident.previousEpisodes ?? 0)} before
                </span>
              )}
            </span>
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

              {seenBefore && (
                <p className={`incident-seen-before${recurring ? ' incident-seen-before--again' : ''}`}>
                  {seenBefore}
                  {incident.lastResolution?.fixRef && (
                    <>
                      {' '}· fix <FixRef value={incident.lastResolution.fixRef} />
                    </>
                  )}
                </p>
              )}

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

              {hasNotes(incident) && editing === null && (
                <div>
                  <h3 className="section-title incident-detail__label">What was learnt</h3>
                  <NotesList notes={incident} />
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

              {editing === 'resolve' && (
                <NotesFormFields
                  id={incident.id}
                  initial={notesFormFrom(incident)}
                  submitLabel={`Resolve #${incident.id}`}
                  pendingLabel="Resolving…"
                  pending={resolve.isPending}
                  note={RESOLVE_WARNING}
                  onSubmit={onResolve}
                  onCancel={() => setEditing(null)}
                />
              )}

              {editing === 'notes' && (
                <NotesFormFields
                  id={incident.id}
                  initial={notesFormFrom(incident)}
                  submitLabel="Save notes"
                  pendingLabel="Saving…"
                  pending={saveNotes.isPending}
                  onSubmit={onSaveNotes}
                  onCancel={() => setEditing(null)}
                />
              )}

              {editing === null && (
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
                  {incident.status !== 'resolved' && (
                    <button
                      type="button"
                      className="btn btn--sm btn--primary"
                      disabled={busy}
                      onClick={() => setEditing('resolve')}
                    >
                      Resolve…
                    </button>
                  )}
                  <button type="button" className="btn btn--sm" disabled={busy} onClick={() => setEditing('notes')}>
                    {hasNotes(incident) ? 'Edit notes' : 'Add notes'}
                  </button>
                  {incident.status !== 'resolved' && (
                    <p className="small-note muted">
                      Resolve only closes the record. If the problem is still there, Sentinel opens a new
                      incident on its next check and alerts again.
                    </p>
                  )}
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
    <div className="tablewrap tablewrap--tall incident-tablewrap">
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

// ---------- history ----------

const HISTORY_COLUMNS = 6

/** One episode of a problem: when, for how long, how it ended, and what was written. */
function EpisodeItem({ episode }: { episode: IncidentEpisode }) {
  const live = episode.status !== 'resolved'
  return (
    <li className="incident-episode">
      <div className="incident-episode__head">
        <SeverityBadge severity={episode.severity} />
        <span className="mono">#{episode.id}</span>
        <span className="mono">{formatDateTime(episode.firstSeenUtc)} IST</span>
        <span className="muted">
          {lastedText(episode)} · seen {occurrencesText(episode.occurrences)}
        </span>
      </div>
      <div className={live ? 'incident-episode__end warn' : 'incident-episode__end'}>
        {endedText(episode)}
        {episode.resolvedUtc && <span className="muted"> · {formatDateTime(episode.resolvedUtc)} IST</span>}
      </div>
      <div className="incident-episode__title">{maskSecrets(episode.title)}</div>
      <NotesList notes={episode} />
    </li>
  )
}

function HistoryRow({ row }: { row: IncidentHistoryRow }) {
  const [open, setOpen] = useState(false)
  const latest = row.latestResolution
  return (
    <>
      <tr
        className={open ? 'incident-row--open' : undefined}
        onClick={() => setOpen((o) => !o)}
        style={{ cursor: 'pointer' }}
      >
        <td>
          <SeverityBadge severity={row.severity} />
        </td>
        <td>
          {/* No handler of its own: Enter/Space fire a click that bubbles to the row. */}
          <button type="button" className="incident-toggle" aria-expanded={open}>
            <span className="incident-toggle__caret" aria-hidden="true">
              {open ? '▾' : '▸'}
            </span>
            <span>
              {maskSecrets(row.title)}
              <span className="small-note muted mono incident-history__fp">{row.fingerprint}</span>
            </span>
          </button>
        </td>
        <td className="mono incident-seen">{episodesText(row)}</td>
        <td
          className="mono incident-seen"
          title={`first ${formatDateTime(row.firstSeenUtc)} · last ${formatDateTime(row.lastSeenUtc)} IST`}
        >
          {formatAge(row.lastSeenUtc)}
        </td>
        <td className="incident-seen">{mttrText(row)}</td>
        <td className="incident-status">
          {row.openNow ? <Badge tone="warn">open now</Badge> : <span className="faint">—</span>}
        </td>
      </tr>

      {open && (
        <tr className="incident-detail">
          <td colSpan={HISTORY_COLUMNS}>
            <div className="incident-detail__body">
              {latest && hasNotes(latest) ? (
                <div>
                  <h3 className="section-title incident-detail__label">
                    Latest notes · #{latest.id}
                    {latest.resolvedUtc ? ` · ${dayText(latest.resolvedUtc)}` : ''}
                  </h3>
                  <NotesList notes={latest} />
                </div>
              ) : (
                <p className="small-note muted" style={{ margin: 0 }}>
                  Nobody has written down a cause or a fix for this yet. Resolve the next episode with notes, or
                  open an incident below and add them.
                </p>
              )}

              <div>
                <h3 className="section-title incident-detail__label">
                  {row.episodes > row.episodeList.length
                    ? `The latest ${row.episodeList.length} of ${row.episodes} episodes`
                    : row.episodes === 1
                      ? 'The one episode'
                      : `All ${row.episodes} episodes`}
                  , newest first
                </h3>
                <ul className="incident-episodes">
                  {row.episodeList.map((e) => (
                    <EpisodeItem key={e.id} episode={e} />
                  ))}
                </ul>
              </div>

              <div className="incident-detail__meta">
                {agentLabel(row.agent)} · <span className="mono">{row.rule}</span> · first in this window{' '}
                {formatDateTime(row.firstSeenUtc)} IST · last {formatDateTime(row.lastSeenUtc)} IST
              </div>
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

/**
 * Every problem of the window, one row per fingerprint, most recurrent first:
 * what a fix is worth most on. A row opens to every episode with its dates,
 * how it ended and the notes, which is the answer to "has this happened
 * before, and what did we do?".
 */
function HistoryPanel() {
  const [days, setDays] = useState(90)
  const history = useIncidentHistory(days)
  const data = history.data

  let body: ReactNode
  if (history.isPending) {
    body = <Loading label="Reading the history…" />
  } else if (!data) {
    body = <InlineError error={history.error} />
  } else if (data.items.length === 0) {
    body = <EmptyState>No incidents were recorded in the last {data.days} days.</EmptyState>
  } else {
    body = (
      <>
        {history.isError && (
          <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
            Refresh failed — showing the history as it was {formatAge(new Date(history.dataUpdatedAt).toISOString())}.
          </p>
        )}
        <div className="tablewrap tablewrap--tall incident-tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th>Worst</th>
                <th>Problem</th>
                <th>How often</th>
                <th>Last seen</th>
                <th>Time to resolve</th>
                <th className="incident-status">Now</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((row) => (
                <HistoryRow key={row.fingerprint} row={row} />
              ))}
            </tbody>
          </table>
        </div>
      </>
    )
  }

  return (
    <Panel
      title={
        <span className="chip-row">
          History{data ? ` — since ${dayText(data.sinceUtc)}` : ''}
          {data && data.items.length > 0 && <span className="faint">{data.items.length}</span>}
        </span>
      }
      actions={
        <div className="seg" role="group" aria-label="Window">
          {HISTORY_WINDOWS.map((w) => (
            <button
              key={w.days}
              type="button"
              className={`seg__btn ${days === w.days ? 'is-active' : ''}`}
              aria-pressed={days === w.days}
              onClick={() => setDays(w.days)}
            >
              {w.label}
            </button>
          ))}
        </div>
      }
    >
      {body}
      <p className="small-note muted">
        One row per problem (its fingerprint), most episodes first. An episode is one incident, from its first
        sighting to its resolve; time to resolve is the mean over the episodes that ended. Times are IST.
      </p>
    </Panel>
  )
}

export function IncidentsPage() {
  const [tab, setTab] = useState<'incidents' | 'history'>('incidents')
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
            production. History shows every problem by how often it came back and what fixed it.
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

      <div className="seg incident-tabs" role="group" aria-label="View">
        <button
          type="button"
          className={`seg__btn ${tab === 'incidents' ? 'is-active' : ''}`}
          aria-pressed={tab === 'incidents'}
          onClick={() => setTab('incidents')}
        >
          Incidents
        </button>
        <button
          type="button"
          className={`seg__btn ${tab === 'history' ? 'is-active' : ''}`}
          aria-pressed={tab === 'history'}
          onClick={() => setTab('history')}
        >
          History
        </button>
      </div>

      {tab === 'history' ? (
        <HistoryPanel />
      ) : (
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
          Resolve closes it, with what caused it and what was done if you know. Times are IST.
        </p>
      </Panel>
      )}
    </div>
  )
}
