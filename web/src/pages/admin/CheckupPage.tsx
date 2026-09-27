/**
 * System → Desk checkup: Sentinel's scheduled health checklist, and a button
 * that asks for one now.
 *
 * Sentinel runs the checklist before the open, after the close, at the end of
 * the day and weekly, and writes each report with a plain-English "what to do"
 * on every item that needs a person. This page shows the latest report with
 * what to do first, the reports before it, and asks for a new one on request.
 * It only reads and asks: nothing here changes the desk.
 *
 * Admin-only, like incidents: the items name accounts, runs and the host.
 */

import { useEffect, useRef, useState } from 'react'
import type { MouseEvent, ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useCheckup, useCheckupLatest, useCheckups, useRunCheckup } from '../../lib/queries'
import {
  WAIT_POLL_MS,
  checkupIdParam,
  countsText,
  emptyToDoText,
  groupItems,
  itemLink,
  pendingText,
  slotLabel,
  staleNote,
  stateBadge,
  verdictBadge,
  waitOutcome,
  waitTimeoutMessage,
} from '../../lib/checkup'
import type { CheckupDetail, CheckupItem, CheckupSummary } from '../../lib/checkup'
import { maskSecrets } from '../../lib/incidents'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import './checkup.css'

const PAGE = '/system/checkups'
const HISTORY_TAKE = 30

/** Re-renders on a clock, so ages and the wait's timeout move between polls that change nothing. */
function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}

function VerdictBadge({ checkup }: { checkup: Pick<CheckupSummary, 'status' | 'verdict'> }) {
  const b = verdictBadge(checkup)
  return <Badge tone={b.tone}>{b.label}</Badge>
}

/** When it finished, or how far it got. */
function whenText(c: CheckupSummary): string {
  if (c.completedUtc) return `completed ${formatDateTime(c.completedUtc)} IST (${formatAge(c.completedUtc)})`
  if (c.startedUtc) return `started ${formatDateTime(c.startedUtc)} IST, not finished`
  if (c.requestedUtc) return `asked for ${formatDateTime(c.requestedUtc)} IST, not started`
  return 'not started'
}

/** The report's header: verdict, headline, and which checkup it is. */
function CheckupHeader({ checkup, isLatest }: { checkup: CheckupDetail; isLatest: boolean }) {
  const headline = maskSecrets(checkup.headline)
  const error = maskSecrets(checkup.error)
  const by = maskSecrets(checkup.requestedBy)
  return (
    <div className="checkup-head">
      <div className="checkup-head__title">
        <VerdictBadge checkup={checkup} />
        <h2 className="checkup-head__headline">{headline || 'No report from this checkup.'}</h2>
      </div>
      <p className="checkup-head__meta">
        {slotLabel(checkup.slot)} · {whenText(checkup)}
        {checkup.host && <> · on <span className="mono">{checkup.host}</span></>}
        {by && <> · asked for by {by}</>}
        {checkup.notifiedUtc && <> · sent to Telegram</>}
      </p>
      {!isLatest && (
        <p className="checkup-head__back">
          Checkup #{checkup.id}, from the history. <Link to={PAGE}>Back to the latest</Link>
        </p>
      )}
      {checkup.status === 'failed' && (
        <div className="alert alert--error" role="status">
          Why it did not finish: {error || 'no reason was recorded.'}
        </div>
      )}
      {checkup.itemsUnreadable && (
        <div className="alert alert--warn" role="status">
          Part of this report could not be read, so the items below may be incomplete. The headline and verdict are
          Sentinel&apos;s own.
        </div>
      )}
    </div>
  )
}

function ItemRow({ item }: { item: CheckupItem }) {
  const badge = stateBadge(item.state)
  const link = itemLink(item.link)
  const action = maskSecrets(item.action)
  const detail = maskSecrets(item.detail)
  return (
    <li className={`checkup-item checkup-item--${badge.tone}`}>
      <div className="checkup-item__head">
        <Badge tone={badge.tone}>{badge.label}</Badge>
        <span className="checkup-item__title">{maskSecrets(item.title) || item.key}</span>
        {item.area && <span className="checkup-item__area">{item.area}</span>}
      </div>
      {detail && <p className="checkup-item__detail">{detail}</p>}
      {action && (
        <p className="checkup-item__action">
          <span className="checkup-item__action-label">What to do:</span> {action}
        </p>
      )}
      {link && (
        <Link className="checkup-item__link" to={link}>
          Open <span className="mono">{link}</span> →
        </Link>
      )}
    </li>
  )
}

function ItemList({ items }: { items: CheckupItem[] }) {
  return (
    <ul className="checkup-items">
      {items.map((item, i) => (
        // Keys are not promised unique: one check can report more than one item.
        <ItemRow key={`${item.key}-${i}`} item={item} />
      ))}
    </ul>
  )
}

/** A section that starts folded: what is fine, and what could not be checked. */
function Folded({ title, items }: { title: string; items: CheckupItem[] }) {
  if (items.length === 0) return null
  return (
    <details className="checkup-section checkup-fold">
      <summary className="section-title checkup-fold__summary">
        {title} ({items.length})
      </summary>
      <ItemList items={items} />
    </details>
  )
}

/** The report's items: what to do first, then what to know, then what is fine and what was not checked. */
function CheckupItems({ checkup }: { checkup: CheckupDetail }) {
  const groups = groupItems(checkup.items)
  if (checkup.items.length === 0 && checkup.status === 'failed') return null
  if (checkup.items.length === 0 && !checkup.itemsUnreadable) {
    return <EmptyState>This checkup recorded no items.</EmptyState>
  }

  return (
    <div className="checkup-sections">
      <section className="checkup-section">
        <h3 className="section-title">To do</h3>
        {groups.toDo.length > 0 ? (
          <ItemList items={groups.toDo} />
        ) : (
          <p className="checkup-none">{emptyToDoText(checkup)}</p>
        )}
      </section>

      {groups.info.length > 0 && (
        <section className="checkup-section">
          <h3 className="section-title">For your information</h3>
          <ItemList items={groups.info} />
        </section>
      )}

      <Folded title="All good" items={groups.ok} />
      <Folded title="Not checked" items={groups.skip} />
    </div>
  )
}

function HistoryPanel({
  rows,
  isPending,
  error,
  refreshFailed,
  selectedId,
  onSelect,
}: {
  rows: CheckupSummary[] | undefined
  isPending: boolean
  error: unknown
  refreshFailed: boolean
  selectedId: number | null
  onSelect: (id: number) => void
}) {
  let body: ReactNode
  if (isPending) {
    body = <Loading label="Reading the history…" />
  } else if (!rows) {
    body = <InlineError error={error} />
  } else if (rows.length === 0) {
    body = <EmptyState>No checkup has been recorded yet.</EmptyState>
  } else {
    // The row is the target for a mouse; the link in its first cell is the
    // same target for a keyboard, and is left to navigate on its own.
    const open = (id: number) => (e: MouseEvent) => {
      if (!(e.target as HTMLElement).closest('a')) onSelect(id)
    }
    body = (
      <>
        {refreshFailed && (
          <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
            Refresh failed — showing the history as it was.
          </p>
        )}
        <div className="tablewrap tablewrap--tall">
          <table className="table table--hover checkup-history">
            <thead>
              <tr>
                <th>When (IST)</th>
                <th>Checkup</th>
                <th>Verdict</th>
                <th>Headline</th>
                <th className="checkup-history__counts">Items</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr
                  key={row.id}
                  className={row.id === selectedId ? 'row--selected' : undefined}
                  onClick={open(row.id)}
                >
                  <td className="mono">
                    <Link to={`${PAGE}?id=${row.id}`} className="checkup-history__link">
                      {formatDateTime(row.completedUtc ?? row.startedUtc ?? row.requestedUtc)}
                    </Link>
                  </td>
                  <td>{slotLabel(row.slot)}</td>
                  <td>
                    <VerdictBadge checkup={row} />
                  </td>
                  {row.headline ? (
                    <td className="checkup-history__headline">{maskSecrets(row.headline)}</td>
                  ) : (
                    <td className="checkup-history__headline checkup-history__headline--none faint">—</td>
                  )}
                  <td className="checkup-history__counts muted">{countsText(row.counts)}</td>
                </tr>
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
          History{rows && rows.length > 0 && <span className="faint">{rows.length}</span>}
        </span>
      }
    >
      {body}
      <p className="small-note muted">
        The last {HISTORY_TAKE} checkups, newest first. Select one to read it in full.
      </p>
    </Panel>
  )
}

export function CheckupPage() {
  const [params, setParams] = useSearchParams()
  const selectedId = checkupIdParam(params.get('id'))

  // A checkup this page asked for and is waiting on: its id and when the wait began.
  const [wait, setWait] = useState<{ runId: number; startedMs: number } | null>(null)
  const [timedOut, setTimedOut] = useState<string | null>(null)
  const now = useNow(wait ? WAIT_POLL_MS : 30_000)

  const latest = useCheckupLatest(wait !== null)
  const pending = latest.data?.pending ?? null
  const history = useCheckups(HISTORY_TAKE, wait !== null || pending !== null)
  const selected = useCheckup(selectedId)
  const run = useRunCheckup()

  const latestId = latest.data?.latest?.id ?? null
  const pendingStatus = pending?.status ?? null

  // A new report has landed (asked for here or scheduled): the history is read
  // again now, not at its next minute, so the two never disagree on screen.
  const refetchHistory = history.refetch
  const seenLatest = useRef<number | null>(null)
  useEffect(() => {
    if (latestId === null) return
    if (seenLatest.current !== null && seenLatest.current !== latestId) void refetchHistory()
    seenLatest.current = latestId
  }, [latestId, refetchHistory])

  useEffect(() => {
    if (!wait) return
    const outcome = waitOutcome({ ...wait, nowMs: now, latestId })
    if (outcome === 'done') {
      setWait(null)
    } else if (outcome === 'timeout') {
      setWait(null)
      setTimedOut(waitTimeoutMessage(pendingStatus))
    }
  }, [wait, now, latestId, pendingStatus])

  function onRun() {
    setTimedOut(null)
    run.mutate(undefined, {
      onSuccess: (answer) => {
        setWait({ runId: answer.id, startedMs: Date.now() })
        // The new report shows where the latest does, so leave any old one picked from the history.
        if (selectedId !== null) setParams({})
      },
    })
  }

  const busy = run.isPending || wait !== null || pending !== null
  // Undefined until the latest has loaded: not known yet says nothing either way.
  const stale = staleNote(latest.data?.lastCompletedUtc, now, formatDateTime)

  let report: ReactNode
  if (selectedId !== null) {
    report = selected.isPending ? (
      <Loading label={`Reading checkup #${selectedId}…`} />
    ) : selected.data ? (
      <>
        <CheckupHeader checkup={selected.data} isLatest={selected.data.id === latestId} />
        <CheckupItems checkup={selected.data} />
      </>
    ) : (
      <>
        <InlineError error={selected.error} />
        <p className="checkup-head__back">
          <Link to={PAGE}>Back to the latest</Link>
        </p>
      </>
    )
  } else if (latest.isPending) {
    report = <Loading label="Reading the latest checkup…" />
  } else if (!latest.data) {
    report = <InlineError error={latest.error} />
  } else if (!latest.data.latest) {
    report = <EmptyState>No checkup has finished yet. The first one Sentinel completes will show here.</EmptyState>
  } else {
    report = (
      <>
        <CheckupHeader checkup={latest.data.latest} isLatest />
        <CheckupItems checkup={latest.data.latest} />
      </>
    )
  }

  return (
    <div className="page">
      <header className="page__header">
        <div className="checkup-intro">
          <h1 className="page__title">Desk checkup</h1>
          <p className="page__subtitle">
            Sentinel&apos;s health checklist for the desk: before the open, after the close, at the end of the day and
            weekly, each item with what to do. It only reads the desk; asking for a checkup changes nothing.
          </p>
        </div>
        <button type="button" className="btn btn--primary checkup-run" disabled={busy} onClick={onRun}>
          {busy ? 'Waiting for Sentinel…' : 'Run a checkup now'}
        </button>
      </header>

      {wait && (
        <p className="checkup-wait" role="status">
          Waiting for Sentinel… the report shows here as soon as it is written. This page looks every 5 s, for up
          to 3 minutes.
        </p>
      )}
      {!wait && pending && (
        <p className="checkup-wait" role="status">
          {pendingText({ ...pending, requestedBy: maskSecrets(pending.requestedBy) })}
        </p>
      )}
      {timedOut && (
        <div className="alert alert--warn" role="status">
          {timedOut}
        </div>
      )}
      {run.error && <InlineError error={run.error} />}
      {stale && (
        <div className="alert alert--warn" role="status">
          {stale}
        </div>
      )}
      {latest.isError && latest.data && (
        <p className="small-note warn" role="status" style={{ margin: 0 }}>
          The latest checkup did not refresh — this is how it was{' '}
          {formatAge(new Date(latest.dataUpdatedAt).toISOString())}.
        </p>
      )}

      <Panel className="checkup-report">{report}</Panel>

      <HistoryPanel
        rows={history.data}
        isPending={history.isPending}
        error={history.error}
        refreshFailed={history.isError}
        selectedId={selectedId}
        onSelect={(id) => setParams({ id: String(id) })}
      />
    </div>
  )
}
