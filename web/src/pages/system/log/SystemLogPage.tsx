/**
 * System → Log: what happened on the platform, as one timeline.
 *
 * Four streams, newest first, with a tab per stream (?source=): Activity,
 * every action that changed something and every refusal, by whoever made it;
 * Alerts, everything the platform sent to Telegram; Deploys, what the desk did
 * with each push; Risk, the kill switch and the limits. They were three pages
 * under two menu groups, plus the log on the Risk page, so "what happened at
 * 09:20" meant opening all four. A stream's own controls show on its tab: who
 * and which module for activity, the Telegram and alerter state for alerts.
 *
 * Reads only, apart from the alerter's start, stop and end-to-end test, which
 * were on the alerts page and stay with the alerts. Admin-only: the activity
 * records admins too.
 */

import { Fragment, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../../../lib/api'
import {
  useActivityFacets,
  useActivityLog,
  useActivityUserSummary,
  useAlertEvents,
  useAlerterStatus,
  useDeployHistory,
  useRiskEvents,
  useStartAlerter,
  useStopAlerter,
} from '../../../lib/queries'
import type { AlerterStatus } from '../../../lib/queries'
import {
  LOG_SOURCES,
  activityRows,
  alertRows,
  deployRows,
  logCounts,
  logSourceFrom,
  mergeLog,
  moduleLabel,
  riskRows,
} from '../../../lib/systemLog'
import type { LogRow, LogSource } from '../../../lib/systemLog'
import type { DeployRecord } from '../../../lib/types'
import { formatAge, formatDateTime, formatTime } from '../../../lib/format'
import { Badge, EmptyState, InlineError } from '../../../components/ui'
import '../health/health.css'
import './log.css'

const SOURCE_LABEL: Record<LogSource, string> = { activity: 'Activity', alerts: 'Alert', deploys: 'Deploy', risk: 'Risk' }

/* ------------------------------------------------------------ activity -- */

function ActivityFilters({
  userId,
  module,
  onlyRefused,
  onUser,
  onModule,
  onRefused,
}: {
  userId: number | null
  module: string
  onlyRefused: boolean
  onUser: (id: number | null) => void
  onModule: (m: string) => void
  onRefused: (v: boolean) => void
}) {
  const facets = useActivityFacets()
  const people = (facets.data?.users ?? []).filter((u) => u.userId != null)
  return (
    <>
      <select
        className="field__input field__input--sm log-select"
        aria-label="Who"
        value={userId ?? ''}
        onChange={(e) => onUser(e.target.value === '' ? null : Number(e.target.value))}
      >
        <option value="">Everyone</option>
        {people.map((u) => (
          <option key={u.userId} value={u.userId!}>
            {u.userName} · {u.count}
            {u.failures > 0 ? ` · ${u.failures} refused` : ''}
          </option>
        ))}
      </select>
      <select className="field__input field__input--sm log-select" aria-label="Module" value={module} onChange={(e) => onModule(e.target.value)}>
        <option value="">Every module</option>
        {(facets.data?.modules ?? []).map((m) => (
          <option key={m.module} value={m.module}>
            {moduleLabel(m.module)} · {m.count}
          </option>
        ))}
      </select>
      <button type="button" className={`btn btn--sm ${onlyRefused ? 'btn--primary' : 'btn--ghost'}`} aria-pressed={onlyRefused} onClick={() => onRefused(!onlyRefused)}>
        Refused only
      </button>
    </>
  )
}

/** Where one person has been: their rollup, in one line under the filters. */
function PersonLine({ userId }: { userId: number }) {
  const summary = useActivityUserSummary(userId)
  const s = summary.data
  if (summary.isError) return <InlineError error={summary.error} />
  if (!s) return null
  return (
    <p className="log-person small">
      <b>{s.total}</b> actions · <span className={s.failures > 0 ? 'warn' : ''}>{s.failures} refused or failed</span> · first seen{' '}
      {s.firstUtc ? formatAge(s.firstUtc) : '—'} · last {s.lastUtc ? formatAge(s.lastUtc) : '—'}
      {s.byModule.length > 0 && (
        <span className="muted"> · {s.byModule.map((m) => `${moduleLabel(m.module)} ${m.count}`).join(' · ')}</span>
      )}
    </p>
  )
}

/* -------------------------------------------------------------- alerts -- */

/** What the platform sends without anyone asking it to. */
const WHAT_GETS_SENT: { label: string; detail: string }[] = [
  { label: 'Strategy run started', detail: 'name, underlying, lots and who started it' },
  { label: 'Strategy run stopped', detail: 'who stopped it and how many positions were squared off' },
  { label: 'Kill switch', detail: 'activated or released, with the reason' },
  { label: 'Strategy signals', detail: 'from the signal alerter, per underlying' },
  { label: 'Candle patterns', detail: 'as candles close, per the rules on Markets → Patterns' },
]

function errorText(err: unknown): string {
  return (
    (err as { body?: { message?: string }; message?: string })?.body?.message ?? (err as { message?: string })?.message ?? 'Failed'
  )
}

/**
 * The alert stream's own state: whether Telegram delivery is set up, and the
 * signal alerter with its start, stop and end-to-end test.
 */
function AlertsStrip({ status }: { status: AlerterStatus | undefined }) {
  const qc = useQueryClient()
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  const start = useStartAlerter()
  const stop = useStopAlerter()
  const said = {
    onSuccess: (res: { message: string }) => setNotice({ ok: true, text: res.message }),
    onError: (err: unknown) => setNotice({ ok: false, text: errorText(err) }),
  }
  const switching = start.isPending || stop.isPending
  const test = useMutation({
    mutationFn: (instrument: string) => api.post<{ message: string }>('/api/Alerts/test-e2e', { instrument }),
    onSuccess: (res) => {
      setNotice({ ok: true, text: res.message })
      // The alert travels Redis → subscriber → database; give it a moment.
      setTimeout(() => qc.invalidateQueries({ queryKey: ['alerts', 'events'] }), 1500)
    },
    onError: (err) => setNotice({ ok: false, text: errorText(err) }),
  })

  if (!status) return <p className="log-strip small muted">Reading the alerter…</p>
  const running = status.processes.filter((p) => p.source !== 'none')
  return (
    <>
      <div className="log-strip small">
        <span>
          Telegram{' '}
          <Badge tone={status.telegramConfigured ? 'pos' : 'warn'}>{status.telegramConfigured ? 'configured' : 'not configured'}</Badge>
        </span>
        <span>
          Signal alerter <Badge tone={status.isRunning ? 'pos' : 'warn'}>{status.isRunning ? 'running' : 'stopped'}</Badge>
          {running.length > 0 && <span className="muted"> {running.map((p) => `${p.underlying}${p.processId ? ` pid ${p.processId}` : ''}`).join(', ')}</span>}
        </span>
        <span className="log-strip__tools">
          <button type="button" className="btn btn--sm btn--ghost" disabled={switching || status.isRunning} onClick={() => start.mutate(undefined, said)}>
            Start
          </button>
          <button type="button" className="btn btn--sm btn--ghost" disabled={switching || !status.isRunning} onClick={() => stop.mutate(undefined, said)}>
            Stop
          </button>
          <button
            type="button"
            className="btn btn--sm btn--ghost"
            disabled={test.isPending}
            title="Sends a mock signal along the whole path, Redis → the API → Telegram and the database, so a silent pipeline is caught here rather than during a trade"
            onClick={() => test.mutate('BANKNIFTY')}
          >
            {test.isPending ? 'Sending…' : 'Send a test alert'}
          </button>
        </span>
      </div>
      {!status.telegramConfigured && (
        <p className="small muted log-note">
          No bot token or chat id on the server, so nothing reaches Telegram; events are still recorded here. Set{' '}
          <code>Telegram:BotToken</code> and <code>Telegram:ChatId</code> to turn delivery on.
        </p>
      )}
      <details className="hp-details log-sent small">
        <summary>What is sent without anyone asking</summary>
        <ul>
          {WHAT_GETS_SENT.map((row) => (
            <li key={row.label}>
              <b>{row.label}</b> <span className="muted">— {row.detail}</span>
            </li>
          ))}
        </ul>
        <p className="muted">
          Delivery never blocks the thing it reports on: if Telegram or Redis is down, the run still starts and the
          failure is logged rather than raised.
        </p>
      </details>
      {notice && (
        <div className={`alert ${notice.ok ? 'alert--success' : 'alert--error'}`} role="status">
          {notice.text}
        </div>
      )}
    </>
  )
}

/* ------------------------------------------------------------ timeline -- */

function DeployDetail({ record }: { record: DeployRecord }) {
  return (
    <div className="log-deploy">
      {record.commits?.length > 0 && (
        <ul className="log-deploy__commits mono">
          {record.commits.slice(0, 6).map((c, i) => (
            <li key={i}>{c}</li>
          ))}
          {record.commits.length > 6 && <li className="faint">…and {record.commits.length - 6} more</li>}
        </ul>
      )}
      {record.steps?.length ? (
        <ol className="log-deploy__steps">
          {record.steps.map((s, i) => (
            <li key={i} className={`log-deploy__step log-deploy__step--${s.status}`}>
              <span className="log-deploy__mark" aria-hidden="true">
                {s.status === 'ok' ? '✓' : s.status === 'failed' ? '✕' : '–'}
              </span>
              <span className="log-deploy__name">{s.name}</span>
              <span className="muted">{s.detail}</span>
              <span className="faint">{s.atUtc ? formatTime(s.atUtc) : ''}</span>
            </li>
          ))}
        </ol>
      ) : (
        <p className="faint small">No steps recorded.</p>
      )}
    </div>
  )
}

function Timeline({ rows, deploys }: { rows: LogRow[]; deploys: Map<string, DeployRecord> }) {
  const [open, setOpen] = useState<string | null>(null)
  return (
    <ol className="log-list">
      {rows.map((r) => {
        const record = deploys.get(r.key)
        const isOpen = open === r.key
        return (
          <Fragment key={r.key}>
            <li className={`log-row log-row--${r.source}`}>
              <time className="log-row__when mono" dateTime={r.atUtc} title={formatAge(r.atUtc)}>
                {formatDateTime(r.atUtc)}
              </time>
              <span className="log-row__source">{SOURCE_LABEL[r.source]}</span>
              <span className="log-row__what">
                {record ? (
                  <button type="button" className="log-row__toggle" aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : r.key)}>
                    {r.title}
                  </button>
                ) : (
                  <span className="log-row__title">{r.title}</span>
                )}
                {r.detail && <span className="log-row__detail">{r.detail}</span>}
              </span>
              <span className="log-row__who">{r.who}</span>
              <span className="log-row__result">{r.result && <Badge tone={r.result.tone}>{r.result.label}</Badge>}</span>
            </li>
            {record && isOpen && (
              <li className="log-row log-row--more">
                <DeployDetail record={record} />
              </li>
            )}
          </Fragment>
        )
      })}
    </ol>
  )
}

/* ---------------------------------------------------------------- page -- */

export function SystemLogPage() {
  const [params, setParams] = useSearchParams()
  const source = logSourceFrom(params.get('source'))
  const [query, setQuery] = useState('')
  const [userId, setUserId] = useState<number | null>(null)
  const [module, setModule] = useState('')
  const [onlyRefused, setOnlyRefused] = useState(false)

  // The activity filters are the server's (it searches the whole log, not the
  // rows on screen); the other streams are short enough to filter here.
  const activity = useActivityLog({
    userId,
    module: module || undefined,
    search: query.trim() || undefined,
    succeeded: onlyRefused ? false : undefined,
    limit: 200,
  })
  const alerts = useAlertEvents(100)
  const alerter = useAlerterStatus()
  const deploys = useDeployHistory(30_000)
  const risk = useRiskEvents(50)

  const all = useMemo(
    () => [...activityRows(activity.data?.rows), ...alertRows(alerts.data), ...deployRows(deploys.data?.entries), ...riskRows(risk.data)],
    [activity.data, alerts.data, deploys.data, risk.data],
  )
  const deployByKey = useMemo(() => {
    const rows = deployRows(deploys.data?.entries)
    return new Map(rows.map((r, i) => [r.key, deploys.data!.entries[i]]))
  }, [deploys.data])
  // The activity rows came back already searched; the query filters the rest.
  const rows = useMemo(() => {
    const searched = mergeLog(all.filter((r) => r.source !== 'activity'), source, query)
    const acts = source === 'all' || source === 'activity' ? all.filter((r) => r.source === 'activity') : []
    return mergeLog([...searched, ...acts], 'all')
  }, [all, source, query])
  const counts = logCounts(all)

  // "Not read yet" and "could not be read" are said, never shown as an empty stream.
  const everyStream: { key: LogSource; pending: boolean; error: unknown }[] = [
    { key: 'activity', pending: activity.isPending, error: activity.isError ? activity.error : null },
    { key: 'alerts', pending: alerts.isPending, error: alerts.isError ? alerts.error : null },
    { key: 'deploys', pending: deploys.isPending, error: deploys.isError ? deploys.error : null },
    { key: 'risk', pending: risk.isPending, error: risk.isError ? risk.error : null },
  ]
  const streams = everyStream.filter((s) => source === 'all' || s.key === source)
  const pending = streams.filter((s) => s.pending).map((s) => s.key)
  const failed = streams.filter((s) => s.error != null)

  const pick = (next: LogSource | 'all') => {
    const p = new URLSearchParams(params)
    if (next === 'all') p.delete('source')
    else p.set('source', next)
    setParams(p, { replace: true })
  }
  const filtered = query.trim() !== '' || userId != null || module !== '' || onlyRefused

  return (
    <div className="page hp log">
      <div className="oc-tabs" role="tablist" aria-label="Stream">
        {([{ key: 'all', label: 'Everything' }, ...LOG_SOURCES] as const).map((s) => (
          <button
            key={s.key}
            type="button"
            role="tab"
            aria-selected={source === s.key}
            className={`oc-tab ${source === s.key ? 'oc-tab--on' : ''}`}
            onClick={() => pick(s.key)}
          >
            {s.label} <span className="muted">· {counts[s.key]}</span>
          </button>
        ))}
      </div>

      <div className="log-tools">
        <input
          className="field__input field__input--sm log-search"
          placeholder="Search the log…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="Search the log"
        />
        {source === 'activity' && (
          <ActivityFilters
            userId={userId}
            module={module}
            onlyRefused={onlyRefused}
            onUser={setUserId}
            onModule={setModule}
            onRefused={setOnlyRefused}
          />
        )}
        {filtered && (
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => {
              setQuery('')
              setUserId(null)
              setModule('')
              setOnlyRefused(false)
            }}
          >
            Clear
          </button>
        )}
        <span className="log-tools__count faint">
          {rows.length} shown
          {pending.length > 0 && ` · reading ${pending.join(', ')}…`}
        </span>
      </div>

      {source === 'activity' && userId != null && <PersonLine userId={userId} />}
      {source === 'alerts' && <AlertsStrip status={alerter.data} />}
      {failed.map((s) => (
        <p key={s.key} className="small warn log-note">
          {LOG_SOURCES.find((x) => x.key === s.key)!.label} could not be read, so it is missing here:{' '}
          {errorText(s.error)}
        </p>
      ))}

      {rows.length === 0 ? (
        pending.length > 0 ? null : (
          <EmptyState>{filtered ? 'Nothing matches those filters.' : 'Nothing recorded yet.'}</EmptyState>
        )
      ) : (
        <Timeline rows={rows} deploys={deployByKey} />
      )}

      <p className="small muted log-note">
        Reads are not recorded: they change nothing and would bury everything that does. Nor are request bodies, which
        carry passwords, broker secrets and tokens. Activity shows the newest 200 rows that match, alerts the newest
        100, risk the newest 50 and deploys the last 20.
      </p>
    </div>
  )
}
