/**
 * Connectors — the directory of data vendors and brokers.
 *
 * Three groups, in the order an operator cares about them: the ones already
 * added, the ones this build can add, and the ones still on the roadmap. Each
 * card opens its own page for credentials, session and the connection probe.
 *
 * The route stays /admin/broker because the OAuth callback redirects into it.
 */

import { Link, useSearchParams } from 'react-router-dom'
import { useIngestorStatuses, useProviderBindings, useProviders } from '../../lib/queries'
import type { Provider } from '../../lib/types'
import { formatAge } from '../../lib/format'
import { Badge, EmptyState, Panel, QueryBoundary } from '../../components/ui'
import { capabilitySummary, isReady, kindLabel, needsCredentials } from '../../lib/providers'

function StatusBadge({ provider }: { provider: Provider }) {
  if (!provider.isInstalled) return <Badge tone="neutral">adapter not installed</Badge>
  if (!needsCredentials(provider)) return <Badge tone="pos">ready · no login</Badge>
  if (!provider.isConfigured) return <Badge tone="warn">credentials needed</Badge>
  if (!provider.session.isConnected) return <Badge tone="warn">not connected</Badge>
  if (provider.session.needsReconnect) return <Badge tone="warn">reconnect due</Badge>
  return <Badge tone="pos">connected</Badge>
}

function ConnectorCard({ provider }: { provider: Provider }) {
  const body = (
    <>
      <span className="connector-card__head">
        <span className="connector-card__mark" aria-hidden="true">
          {provider.displayName.slice(0, 2).toUpperCase()}
        </span>
        <span>
          <span className="module-card__name">
            {provider.displayName}
            <StatusBadge provider={provider} />
          </span>
          <span className="connector-card__kind">
            {kindLabel(provider.kind)} · <span className="mono">{provider.key}</span>
          </span>
        </span>
      </span>

      {provider.isInstalled ? (
        <>
          <p className="module-card__desc">{capabilitySummary(provider)}</p>
          <p className="small-note muted">
            {!needsCredentials(provider) ? (
              <>No credentials needed</>
            ) : provider.isConfigured ? (
              <>
                Credentials {provider.credentials.source === 'config' ? 'from .env' : 'saved'}
                {provider.session.isConnected && (
                  <> · token {formatAge(provider.session.connectedUtc)}</>
                )}
              </>
            ) : (
              <>No credentials yet — open to add them.</>
            )}
            {provider.servingCapabilities.length > 0 && (
              <> · serving {provider.servingCapabilities.join(', ')}</>
            )}
          </p>
        </>
      ) : (
        <p className="module-card__desc">{provider.plannedNote}</p>
      )}
    </>
  )

  return provider.isInstalled ? (
    <Link to={`/admin/broker/${provider.key}`} className="module-card connector-card">
      {body}
    </Link>
  ) : (
    <div className="module-card connector-card module-card--off">{body}</div>
  )
}

function RoutingPanel() {
  const bindings = useProviderBindings()

  return (
    <Panel title="Routing — who serves what">
      <QueryBoundary query={bindings} empty="No capabilities are registered.">
        {(rows) => (
          <div className="tablewrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Capability</th>
                  <th>Connector chain</th>
                  <th>Source of this decision</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.capability}>
                    <td>{row.capability}</td>
                    <td className="mono">
                      {row.providerKeys.length > 0 ? (
                        row.providerKeys.join('  →  ')
                      ) : (
                        <span className="muted">nothing available</span>
                      )}
                    </td>
                    <td>
                      {row.isFallback ? (
                        <Badge tone="neutral">automatic</Badge>
                      ) : (
                        <Badge tone="accent">configured</Badge>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryBoundary>
      <p className="small-note muted">
        With one connector installed there is nothing to choose, so every row reads <i>automatic</i>:
        the platform uses whichever connector claims the capability. Once a second connector ships, a
        chain can be pinned per capability and the rest of it becomes the failover order. Order
        routing never fails over on its own — a broker that timed out may already have accepted the
        order.
      </p>
    </Panel>
  )
}

export function BrokerPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const providers = useProviders()
  const ingestors = useIngestorStatuses()

  const connected = searchParams.get('connected')
  const reason = searchParams.get('reason')

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Connectors</h1>
        <p className="page__subtitle">
          Every data vendor and broker the platform can talk to. Open one to add its credentials,
          connect it and test that real data comes back.
        </p>
      </header>

      {connected === '1' && (
        <div className="alert alert--success" role="status">
          Connected — the token is saved.
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setSearchParams({}, { replace: true })}
          >
            Dismiss
          </button>
        </div>
      )}
      {connected === '0' && (
        <div className="alert alert--error" role="alert">
          Connection failed{reason ? `: ${reason}` : '.'}
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setSearchParams({}, { replace: true })}
          >
            Dismiss
          </button>
        </div>
      )}

      <QueryBoundary query={providers} empty="This build ships no connectors.">
        {(list) => {
          const active = list.filter(isReady)
          const available = list.filter((p) => p.isInstalled && !isReady(p))
          const planned = list.filter((p) => !p.isInstalled)

          return (
            <>
              <Panel title={`Active (${active.length})`}>
                {active.length === 0 ? (
                  <EmptyState>
                    Nothing usable yet. Pick one from “Available to add” below and save its
                    credentials.
                  </EmptyState>
                ) : (
                  <div className="module-grid">
                    {active.map((p) => (
                      <ConnectorCard key={p.key} provider={p} />
                    ))}
                  </div>
                )}
              </Panel>

              {available.length > 0 && (
                <Panel title={`Available to add (${available.length})`}>
                  <div className="module-grid">
                    {available.map((p) => (
                      <ConnectorCard key={p.key} provider={p} />
                    ))}
                  </div>
                </Panel>
              )}

              {planned.length > 0 && (
                <Panel title={`Planned (${planned.length})`}>
                  <p className="muted" style={{ maxWidth: '78ch' }}>
                    These have no adapter in this build, so there is nothing to configure yet. A
                    connector is code, not a configuration row: a new vendor needs an adapter that
                    speaks its API, declares what it can deliver, and maps its symbols to the
                    platform's. Once one ships it joins the list above and everything on this page
                    works for it unchanged.
                  </p>
                  <div className="module-grid">
                    {planned.map((p) => (
                      <ConnectorCard key={p.key} provider={p} />
                    ))}
                  </div>
                </Panel>
              )}
            </>
          )
        }}
      </QueryBoundary>

      <RoutingPanel />

      <Panel title="After connecting — start the data">
        <ol className="broker-steps">
          <li>
            <b>Start the Python ingestor</b> on the API host:
            <code>cd src/AlgoTrading.PythonEngine &amp;&amp; python algo.py</code> → live stream. It
            reads the watchlist from the API and begins pushing ticks.
          </li>
          <li>
            <b>Watch it come alive</b> — the ingestor heartbeat below should turn healthy within a
            minute, and Watchlist quotes start updating.
          </li>
          <li>
            <b>Optional:</b> run a daily-candle backfill under Data ingestion to fill historical
            charts.
          </li>
        </ol>
        <QueryBoundary query={ingestors} empty="No ingestor heartbeat recorded yet.">
          {(list) => (
            <div className="chip-row">
              {list.map((s) => (
                <span key={s.sourceName} className="badge badge--neutral mono">
                  {s.sourceName}
                  {'  '}
                  <Badge tone={s.isHealthy ? 'pos' : 'warn'}>
                    {s.isHealthy ? 'healthy' : `stale · ${formatAge(s.lastHeartbeatUtc)}`}
                  </Badge>
                </span>
              ))}
            </div>
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
