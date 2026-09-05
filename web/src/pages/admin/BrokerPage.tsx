/**
 * Connectors — every data vendor and broker this build ships.
 *
 * One card per connector: what it can actually deliver, whose credentials it
 * uses, whether it is connected, and a probe that fetches real bars so "saved"
 * and "working" are never confused. Below them, the routing table that decides
 * which connector serves which job.
 *
 * The route stays /admin/broker because the OAuth callback redirects here.
 */

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { api } from '../../lib/api'
import {
  useDisconnectProvider,
  useIngestorStatuses,
  useProviderBindings,
  useProviders,
  useSaveProviderCredentials,
  useTestProvider,
} from '../../lib/queries'
import type { Provider, ProviderTestResult } from '../../lib/types'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'

/** The capability flags, in the order an operator thinks about them. */
const CAPABILITY_LABELS: { key: keyof Provider['capabilities']; label: string }[] = [
  { key: 'history', label: 'History' },
  { key: 'liveTicks', label: 'Live ticks' },
  { key: 'quotes', label: 'Quotes' },
  { key: 'optionChain', label: 'Option chain' },
  { key: 'depth', label: 'Bid/ask depth' },
  { key: 'openInterest', label: 'Open interest' },
  { key: 'greeks', label: 'Greeks' },
  { key: 'orders', label: 'Orders' },
]

function KindBadge({ kind }: { kind: Provider['kind'] }) {
  const label = kind === 'Both' ? 'Data + Broker' : kind === 'Data' ? 'Data vendor' : 'Broker'
  return <Badge tone="accent">{label}</Badge>
}

function StatusBadge({ provider }: { provider: Provider }) {
  if (provider.auth === 'None') return <Badge tone="pos">No login needed</Badge>
  if (!provider.session.isConnected) return <Badge tone="warn">Not connected</Badge>
  if (provider.session.needsReconnect) return <Badge tone="warn">Token stale — reconnect</Badge>
  return <Badge tone="pos">Connected</Badge>
}

function CredentialsSourceBadge({ source, updatedBy }: { source: string; updatedBy: string | null }) {
  if (source === 'database') return <Badge tone="pos">saved by {updatedBy ?? 'admin'}</Badge>
  if (source === 'config') return <Badge tone="accent">server configuration (.env)</Badge>
  return <Badge tone="warn">not configured</Badge>
}

function ConnectorCard({ provider }: { provider: Provider }) {
  const saveCredentials = useSaveProviderCredentials()
  const disconnect = useDisconnectProvider()
  const test = useTestProvider()
  const [connectError, setConnectError] = useState<unknown>(null)
  const [result, setResult] = useState<ProviderTestResult | null>(null)

  const [form, setForm] = useState({ clientId: '', secretKey: '', redirectUri: '' })

  // Pre-fill from whatever the server already knows; the secret is never
  // returned, so that field always starts empty.
  useEffect(() => {
    setForm((f) => ({
      clientId: f.clientId || provider.credentials.clientId,
      secretKey: f.secretKey,
      redirectUri: f.redirectUri || provider.credentials.redirectUri || provider.suggestedRedirectUri,
    }))
  }, [provider])

  const connect = useMutation({
    mutationFn: () => api.get<{ authUrl: string }>(`/api/Providers/${provider.key}/auth-url`),
    onSuccess: ({ authUrl }) => {
      // Leave the SPA: vendor hosted login → API callback → back to this page.
      window.location.assign(authUrl)
    },
    onError: setConnectError,
  })

  const credentialsMissing = provider.credentials.source === 'none'
  const needsLogin = provider.auth !== 'None'

  return (
    <Panel
      title={
        <span className="chip-row">
          {provider.displayName}
          <span className="mono muted">{provider.key}</span>
          <KindBadge kind={provider.kind} />
          <StatusBadge provider={provider} />
        </span>
      }
    >
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              {CAPABILITY_LABELS.map((c) => (
                <th key={c.key}>{c.label}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            <tr>
              {CAPABILITY_LABELS.map((c) => (
                <td key={c.key}>
                  {provider.capabilities[c.key] ? (
                    <Badge tone="pos">yes</Badge>
                  ) : (
                    <span className="muted">—</span>
                  )}
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>
      <p className="small-note muted">
        Declared by the connector itself, so a strategy that needs open interest can be told before
        it runs instead of finding nulls at runtime.
        {provider.capabilities.maxStreamSymbols != null && (
          <> Streams up to {provider.capabilities.maxStreamSymbols} symbols.</>
        )}
        {provider.capabilities.segments.length > 0 && (
          <> Segments: {provider.capabilities.segments.join(', ')}.</>
        )}
        {provider.servingCapabilities.length > 0 && (
          <> Currently serving: <b>{provider.servingCapabilities.join(', ')}</b>.</>
        )}
      </p>

      {needsLogin && (
        <>
          <h3 className="section-title connector-section">App credentials</h3>
          <p className="muted" style={{ maxWidth: '78ch' }}>
            Each installation uses its <b>own</b> {provider.displayName} app. Register the redirect URL{' '}
            <code>{provider.suggestedRedirectUri}</code> with the vendor, then save the app id and
            secret here — stored encrypted in this installation's database, never in the repository.
          </p>
          <p className="small-note muted">
            Current source:{' '}
            <CredentialsSourceBadge
              source={provider.credentials.source}
              updatedBy={provider.credentials.updatedBy}
            />
          </p>
          {saveCredentials.isError && <InlineError error={saveCredentials.error} />}
          <form
            className="form-row"
            onSubmit={(e) => {
              e.preventDefault()
              saveCredentials.mutate(
                { providerKey: provider.key, ...form },
                { onSuccess: () => setForm((f) => ({ ...f, secretKey: '' })) },
              )
            }}
          >
            <div className="field">
              <label className="field__label" htmlFor={`${provider.key}-client`}>
                App id (client id)
              </label>
              <input
                id={`${provider.key}-client`}
                className="field__input"
                required
                value={form.clientId}
                onChange={(e) => setForm({ ...form, clientId: e.target.value })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor={`${provider.key}-secret`}>
                Secret key
              </label>
              <input
                id={`${provider.key}-secret`}
                className="field__input"
                type="password"
                required
                placeholder={provider.credentials.hasSecret ? 'saved — re-enter to change' : ''}
                value={form.secretKey}
                onChange={(e) => setForm({ ...form, secretKey: e.target.value })}
                autoComplete="new-password"
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor={`${provider.key}-redirect`}>
                Redirect URL (register with the vendor)
              </label>
              <input
                id={`${provider.key}-redirect`}
                className="field__input"
                required
                value={form.redirectUri}
                onChange={(e) => setForm({ ...form, redirectUri: e.target.value })}
              />
            </div>
            <button className="btn btn--primary" disabled={saveCredentials.isPending}>
              {saveCredentials.isPending ? 'Saving…' : 'Save credentials'}
            </button>
          </form>
        </>
      )}

      <h3 className="section-title connector-section">Session</h3>
      {connectError != null && <InlineError error={connectError} />}
      {disconnect.isError && <InlineError error={disconnect.error} />}
      <div className="broker-state">
        <div>
          <div className={`killswitch__state ${provider.session.isConnected && !provider.session.needsReconnect ? 'pos' : 'warn'}`}>
            {!needsLogin
              ? 'Always available'
              : provider.session.isConnected
                ? provider.session.needsReconnect
                  ? 'Token is from a previous day'
                  : 'Connected'
                : 'Not connected'}
          </div>
          <p className="muted">
            {!needsLogin ? (
              <>This connector needs no login — it reads what the platform already has.</>
            ) : provider.session.isConnected ? (
              <>
                Token saved {formatAge(provider.session.connectedUtc)} (
                {formatDateTime(provider.session.connectedUtc)}).
                {provider.auth === 'OAuthDaily' && ' Tokens expire daily — reconnect each trading morning.'}
              </>
            ) : (
              <>No usable token. Connect to authorise this platform against your account.</>
            )}
          </p>
        </div>
        <div className="broker-state__actions">
          {needsLogin && provider.isBroker && (
            <button
              type="button"
              className="btn btn--primary"
              disabled={connect.isPending || credentialsMissing}
              title={credentialsMissing ? 'Save the app credentials above first' : undefined}
              onClick={() => {
                setConnectError(null)
                connect.mutate()
              }}
            >
              {connect.isPending
                ? 'Redirecting…'
                : provider.session.isConnected
                  ? `Reconnect ${provider.displayName}`
                  : `Connect ${provider.displayName}`}
            </button>
          )}
          <button
            type="button"
            className="btn btn--ghost"
            disabled={test.isPending}
            onClick={() => {
              setResult(null)
              test.mutate(provider.key, { onSuccess: setResult })
            }}
          >
            {test.isPending ? 'Testing…' : 'Test connection'}
          </button>
          {needsLogin && provider.session.isConnected && (
            <button
              type="button"
              className="btn btn--ghost"
              disabled={disconnect.isPending}
              onClick={() => disconnect.mutate(provider.key)}
            >
              Disconnect
            </button>
          )}
        </div>
      </div>
      {test.isError && <InlineError error={test.error} />}
      {result && (
        <div className={`alert ${result.ok ? 'alert--success' : 'alert--error'}`} role="status">
          <b>{result.probe}</b> — {result.message} <span className="muted">({result.elapsedMs} ms)</span>
        </div>
      )}
    </Panel>
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
                      {row.providerKeys.length > 0 ? row.providerKeys.join('  →  ') : <span className="muted">nothing available</span>}
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
        With one connector registered there is nothing to choose, so every row reads{' '}
        <i>automatic</i>: the platform uses whichever connector claims the capability. Once a second
        connector ships, a chain can be pinned per capability and the rest of it becomes the failover
        order. Order routing never fails over on its own — a broker that timed out may already have
        accepted the order.
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

  function dismissBanner() {
    setSearchParams({}, { replace: true })
  }

  const summary = useMemo(() => {
    const list = providers.data ?? []
    const needingLogin = list.filter((p) => p.auth !== 'None')
    const live = needingLogin.filter((p) => p.session.isConnected && !p.session.needsReconnect)
    return { total: list.length, needingLogin: needingLogin.length, live: live.length }
  }, [providers.data])

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Connectors</h1>
        <p className="page__subtitle">
          Data vendors and brokers. {summary.total} registered
          {summary.needingLogin > 0 && <> · {summary.live}/{summary.needingLogin} logged in</>}.
          Credentials, sessions and routing are all configured from here.
        </p>
      </header>

      {connected === '1' && (
        <div className="alert alert--success" role="status">
          Connected — the token is saved. You can start the ingestor now.
          <button type="button" className="btn btn--ghost btn--sm" onClick={dismissBanner}>
            Dismiss
          </button>
        </div>
      )}
      {connected === '0' && (
        <div className="alert alert--error" role="alert">
          Connection failed{reason ? `: ${reason}` : '.'}
          <button type="button" className="btn btn--ghost btn--sm" onClick={dismissBanner}>
            Dismiss
          </button>
        </div>
      )}

      <QueryBoundary query={providers} empty="This build ships no connectors.">
        {(list) => (
          <>
            {list.map((provider) => (
              <ConnectorCard key={provider.key} provider={provider} />
            ))}
          </>
        )}
      </QueryBoundary>

      <RoutingPanel />

      <Panel title="Adding another vendor">
        <p className="muted" style={{ maxWidth: '78ch' }}>
          A connector is code, not a configuration row: a new vendor needs an adapter that speaks its
          API, declares what it can deliver, and translates its symbols to the platform's. Once that
          adapter ships it appears on this page like the ones above — credentials, connect, test and
          routing all work without any further change. The foundation for that is in place; the next
          adapters planned are a replay source over this platform's own stored candles, a CSV source,
          and then Dhan.
        </p>
      </Panel>

      <Panel title="After connecting — start the data">
        <ol className="broker-steps">
          <li>
            <b>Start the Python ingestor</b> on the API host:
            <code>cd src/AlgoTrading.PythonEngine &amp;&amp; python algo.py</code> → live stream.
            It reads the watchlist from the API and begins pushing ticks.
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
