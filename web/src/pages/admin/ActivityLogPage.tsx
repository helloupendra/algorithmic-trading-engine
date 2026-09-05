/**
 * Activity log — who did what, across every module.
 *
 * Two panes: the people on the left, their actions on the right. Clicking a
 * person filters everything to them and shows a rollup of where they have been.
 * That is the question this page exists to answer; a flat firehose is what every
 * other log view already gives you.
 *
 * Admin-only, because it records admins too.
 */

import { useState } from 'react'
import {
  useActivityFacets,
  useActivityLog,
  useActivityUserSummary,
} from '../../lib/queries'
import type { ActivityLogEntry } from '../../lib/types'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

const MODULE_LABELS: Record<string, string> = {
  strategies: 'Strategies',
  backtesting: 'Backtesting',
  data: 'Data',
  connectors: 'Connectors',
  risk: 'Risk',
  alerts: 'Alerts',
  users: 'Users',
  auth: 'Sign-in',
  other: 'Other',
}

function moduleLabel(key: string) {
  return MODULE_LABELS[key] ?? key
}

/** What the row actually says happened. */
function describe(entry: ActivityLogEntry): string {
  if (entry.summary) return entry.summary
  return `${entry.method} ${entry.path}`
}

function StatusCell({ entry }: { entry: ActivityLogEntry }) {
  if (entry.succeeded) return <Badge tone="pos">{entry.statusCode}</Badge>
  if (entry.statusCode === 403) return <Badge tone="warn">403 refused</Badge>
  if (entry.statusCode === 401) return <Badge tone="warn">401</Badge>
  return <Badge tone="neg">{entry.statusCode}</Badge>
}

function UserDetail({ userId, userName }: { userId: number; userName: string }) {
  const summary = useActivityUserSummary(userId)

  return (
    <QueryBoundary query={summary}>
      {(data) =>
        data.total === 0 ? (
          <EmptyState>Nothing recorded for {userName} yet.</EmptyState>
        ) : (
          <>
            <div className="stat-grid">
              <div className="stat">
                <div className="stat__label">Actions</div>
                <div className="stat__value">{data.total}</div>
              </div>
              <div className="stat">
                <div className="stat__label">Refused or failed</div>
                <div className={`stat__value ${data.failures > 0 ? 'warn' : ''}`}>
                  {data.failures}
                </div>
              </div>
              <div className="stat">
                <div className="stat__label">First seen</div>
                <div className="stat__value">{data.firstUtc ? formatAge(data.firstUtc) : '—'}</div>
              </div>
              <div className="stat">
                <div className="stat__label">Last seen</div>
                <div className="stat__value">{data.lastUtc ? formatAge(data.lastUtc) : '—'}</div>
              </div>
            </div>

            <div className="tablewrap" style={{ marginTop: 10 }}>
              <table className="table">
                <thead>
                  <tr>
                    <th>Module</th>
                    <th className="r">Actions</th>
                    <th className="r">Refused</th>
                    <th>Last</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byModule.map((m) => (
                    <tr key={m.module}>
                      <td>{moduleLabel(m.module)}</td>
                      <td className="r">{m.count}</td>
                      <td className={`r ${m.failures > 0 ? 'warn' : ''}`}>{m.failures}</td>
                      <td>{formatAge(m.lastUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )
      }
    </QueryBoundary>
  )
}

export function ActivityLogPage() {
  const facets = useActivityFacets()
  const [userId, setUserId] = useState<number | null>(null)
  const [userName, setUserName] = useState<string>('')
  const [module, setModule] = useState('')
  const [search, setSearch] = useState('')
  const [onlyFailures, setOnlyFailures] = useState(false)

  const log = useActivityLog({
    userId,
    module: module || undefined,
    search: search || undefined,
    succeeded: onlyFailures ? false : undefined,
    limit: 200,
  })

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Activity log</h1>
        <p className="page__subtitle">
          Every action that changed something, by whoever made it — admin, trader or the engine's own
          account. Refusals are recorded too: a 403 is often the more interesting row.
        </p>
      </header>

      <div className="two-col">
        <Panel title="Who">
          <QueryBoundary query={facets}>
            {(data) => (
              <>
                <button
                  type="button"
                  className={`btn btn--sm ${userId === null ? 'btn--primary' : 'btn--ghost'}`}
                  onClick={() => {
                    setUserId(null)
                    setUserName('')
                  }}
                  style={{ marginBottom: 8 }}
                >
                  Everyone
                </button>
                {data.users.length === 0 ? (
                  <EmptyState>Nothing recorded yet.</EmptyState>
                ) : (
                  <div className="tablewrap">
                    <table className="table">
                      <thead>
                        <tr>
                          <th>Account</th>
                          <th className="r">Actions</th>
                          <th className="r">Refused</th>
                          <th>Last</th>
                        </tr>
                      </thead>
                      <tbody>
                        {data.users.map((u) => (
                          <tr
                            key={`${u.userId ?? 'anon'}-${u.userName}`}
                            className={userId === u.userId ? 'row--selected' : undefined}
                          >
                            <td>
                              {u.userId == null ? (
                                <span className="muted">{u.userName}</span>
                              ) : (
                                <button
                                  type="button"
                                  className="btn btn--ghost btn--sm"
                                  onClick={() => {
                                    setUserId(u.userId)
                                    setUserName(u.userName)
                                  }}
                                >
                                  {u.userName}
                                </button>
                              )}
                            </td>
                            <td className="r">{u.count}</td>
                            <td className={`r ${u.failures > 0 ? 'warn' : ''}`}>{u.failures}</td>
                            <td>{formatAge(u.lastUtc)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <p className="small-note muted">
                  Click an account to narrow everything to them. Anonymous rows are requests with no
                  session — a failed sign-in, or the broker's OAuth callback.
                </p>
              </>
            )}
          </QueryBoundary>
        </Panel>

        <Panel title={userId ? `${userName} — where they have been` : 'Modules'}>
          {userId ? (
            <UserDetail userId={userId} userName={userName} />
          ) : (
            <QueryBoundary query={facets}>
              {(data) =>
                data.modules.length === 0 ? (
                  <EmptyState>Nothing recorded yet.</EmptyState>
                ) : (
                  <div className="chip-row" style={{ flexWrap: 'wrap' }}>
                    {data.modules.map((m) => (
                      <button
                        key={m.module}
                        type="button"
                        className={`btn btn--sm ${module === m.module ? 'btn--primary' : 'btn--ghost'}`}
                        onClick={() => setModule(module === m.module ? '' : m.module)}
                      >
                        {moduleLabel(m.module)} · {m.count}
                      </button>
                    ))}
                  </div>
                )
              }
            </QueryBoundary>
          )}
        </Panel>
      </div>

      <Panel
        title={
          <span className="chip-row">
            Actions
            {userId && <Badge tone="accent">{userName}</Badge>}
            {module && <Badge tone="accent">{moduleLabel(module)}</Badge>}
            {onlyFailures && <Badge tone="warn">refused only</Badge>}
          </span>
        }
        actions={
          <div className="chip-row">
            <input
              className="field__input"
              placeholder="path, summary or user…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              aria-label="Search the log"
            />
            <button
              type="button"
              className={`btn btn--sm ${onlyFailures ? 'btn--primary' : 'btn--ghost'}`}
              onClick={() => setOnlyFailures((f) => !f)}
            >
              Refused only
            </button>
            {(userId || module || search || onlyFailures) && (
              <button
                type="button"
                className="btn btn--ghost btn--sm"
                onClick={() => {
                  setUserId(null)
                  setUserName('')
                  setModule('')
                  setSearch('')
                  setOnlyFailures(false)
                }}
              >
                Clear
              </button>
            )}
          </div>
        }
      >
        {log.isError && <InlineError error={log.error} />}
        <QueryBoundary query={log}>
          {(page) =>
            page.rows.length === 0 ? (
              <EmptyState>Nothing matches those filters.</EmptyState>
            ) : (
              <>
                <p className="small-note muted">
                  Showing {page.rows.length} of {page.total}.
                </p>
                <div className="tablewrap tablewrap--tall">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>When</th>
                        <th>Who</th>
                        <th>Module</th>
                        <th>What happened</th>
                        <th>Result</th>
                        <th className="r">Took</th>
                      </tr>
                    </thead>
                    <tbody>
                      {page.rows.map((entry) => (
                        <tr key={entry.id}>
                          <td className="mono" title={formatDateTime(entry.occurredUtc)}>
                            {formatAge(entry.occurredUtc)}
                          </td>
                          <td>
                            {entry.userName}
                            {entry.role && <div className="small-note muted">{entry.role}</div>}
                          </td>
                          <td>{moduleLabel(entry.module)}</td>
                          <td>
                            {describe(entry)}
                            {entry.summary && (
                              <div className="small-note muted mono">
                                {entry.method} {entry.path}
                              </div>
                            )}
                          </td>
                          <td>
                            <StatusCell entry={entry} />
                          </td>
                          <td className="r mono">{entry.durationMs} ms</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )
          }
        </QueryBoundary>
        <p className="small-note muted">
          Reads are not recorded — they change nothing and would bury everything that does. Nor are
          request bodies: they carry passwords, broker secrets and tokens, and an audit log that leaks
          credentials is worse than none. The engine's own high-frequency tick and heartbeat posts are
          excluded for the same reason of signal over noise.
        </p>
      </Panel>
    </div>
  )
}
