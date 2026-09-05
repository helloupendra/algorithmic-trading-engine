/**
 * One connector, in full: what it can deliver, its app credentials, its session,
 * and a probe that fetches real bars so "saved" and "working" are never confused.
 *
 * Reached from the Connectors directory, and from the OAuth callback — the API
 * redirects to /admin/broker/{key} so the operator lands where they pressed
 * Connect.
 */

import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { api } from '../../lib/api'
import {
  useDisconnectProvider,
  useProviderBindings,
  useProviders,
  useSaveProviderCredentials,
  useTestProvider,
} from '../../lib/queries'
import type { Provider, ProviderTestResult } from '../../lib/types'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { CAPABILITY_LABELS, kindLabel } from '../../lib/providers'

function CredentialsForm({ provider }: { provider: Provider }) {
  const saveCredentials = useSaveProviderCredentials()
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

  return (
    <Panel title="App credentials">
      <p className="muted" style={{ maxWidth: '78ch' }}>
        Each installation uses its <b>own</b> {provider.displayName} app. Register the redirect URL{' '}
        <code>{provider.suggestedRedirectUri}</code> with the vendor, then save the app id and secret
        here — stored encrypted in this installation's database, never in the repository.
      </p>
      <p className="small-note muted">
        Current source:{' '}
        {provider.credentials.source === 'database' ? (
          <Badge tone="pos">saved by {provider.credentials.updatedBy ?? 'admin'}</Badge>
        ) : provider.credentials.source === 'config' ? (
          <Badge tone="accent">server configuration (.env)</Badge>
        ) : (
          <Badge tone="warn">not configured</Badge>
        )}
        {provider.credentials.updatedUtc && <> · updated {formatAge(provider.credentials.updatedUtc)}</>}
      </p>
      {saveCredentials.isError && <InlineError error={saveCredentials.error} />}
      {saveCredentials.isSuccess && (
        <div className="alert alert--success" role="status">
          Credentials saved — you can connect now.
        </div>
      )}
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
          <label className="field__label" htmlFor="cd-client">
            App id (client id)
          </label>
          <input
            id="cd-client"
            className="field__input"
            required
            value={form.clientId}
            onChange={(e) => setForm({ ...form, clientId: e.target.value })}
          />
        </div>
        <div className="field">
          <label className="field__label" htmlFor="cd-secret">
            Secret key
          </label>
          <input
            id="cd-secret"
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
          <label className="field__label" htmlFor="cd-redirect">
            Redirect URL (register with the vendor)
          </label>
          <input
            id="cd-redirect"
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
    </Panel>
  )
}

function SessionPanel({ provider }: { provider: Provider }) {
  const disconnect = useDisconnectProvider()
  const test = useTestProvider()
  const [connectError, setConnectError] = useState<unknown>(null)
  const [result, setResult] = useState<ProviderTestResult | null>(null)

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
    <Panel title="Session">
      {connectError != null && <InlineError error={connectError} />}
      {disconnect.isError && <InlineError error={disconnect.error} />}
      <div className="broker-state">
        <div>
          <div
            className={`killswitch__state ${
              !needsLogin || (provider.session.isConnected && !provider.session.needsReconnect)
                ? 'pos'
                : 'warn'
            }`}
          >
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
                {provider.auth === 'OAuthDaily' &&
                  ' Tokens expire daily — reconnect each trading morning.'}
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
          <b>{result.probe}</b> — {result.message}{' '}
          <span className="muted">({result.elapsedMs} ms)</span>
        </div>
      )}
    </Panel>
  )
}

export function ConnectorDetailPage() {
  const { providerKey = '' } = useParams()
  const [searchParams, setSearchParams] = useSearchParams()
  const providers = useProviders()
  const bindings = useProviderBindings()

  const provider = providers.data?.find((p) => p.key === providerKey)

  const connected = searchParams.get('connected')
  const reason = searchParams.get('reason')

  if (providers.isLoading) return <Loading label="Loading connector…" />
  if (providers.isError) return <InlineError error={providers.error} />

  if (!provider) {
    return (
      <div className="page">
        <header className="page__header">
          <h1 className="page__title">Connector not found</h1>
        </header>
        <EmptyState>
          No connector is registered under <code>{providerKey}</code>.{' '}
          <Link to="/admin/broker">Back to connectors</Link>.
        </EmptyState>
      </div>
    )
  }

  const servingHere = (bindings.data ?? []).filter((b) => b.providerKeys.includes(provider.key))

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">
          {provider.displayName} <span className="mono muted">{provider.key}</span>
        </h1>
        <p className="page__subtitle">
          {kindLabel(provider.kind)}
          {provider.auth === 'OAuthDaily' && ' · daily OAuth login'}
          {provider.auth === 'ApiKey' && ' · API key'}
        </p>
      </header>

      <p className="small-note">
        <Link to="/admin/broker">← All connectors</Link>
      </p>

      {connected === '1' && (
        <div className="alert alert--success" role="status">
          {provider.displayName} connected — the token is saved. You can start the ingestor now.
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
          {provider.displayName} connection failed{reason ? `: ${reason}` : '.'}
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setSearchParams({}, { replace: true })}
          >
            Dismiss
          </button>
        </div>
      )}

      {!provider.isInstalled && (
        <Panel title="Adapter not installed">
          <p className="muted" style={{ maxWidth: '78ch' }}>
            {provider.plannedNote} There is nothing to configure until the adapter ships — a connector
            is code, not a configuration row.
          </p>
        </Panel>
      )}

      {provider.isInstalled && (
        <>
          <Panel title="What it can deliver">
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
              Declared by the connector itself, so a strategy that needs open interest can be told
              before it runs instead of finding nulls at runtime.
            </p>
            <div className="kv-grid">
              {provider.capabilities.maxStreamSymbols != null && (
                <div>
                  <span>Stream limit</span>
                  <span>{provider.capabilities.maxStreamSymbols} symbols</span>
                </div>
              )}
              {provider.capabilities.historyMaxDaysPerCall != null && (
                <div>
                  <span>History window</span>
                  <span>{provider.capabilities.historyMaxDaysPerCall} days per call</span>
                </div>
              )}
              {provider.capabilities.segments.length > 0 && (
                <div>
                  <span>Segments</span>
                  <span>{provider.capabilities.segments.join(', ')}</span>
                </div>
              )}
              {provider.capabilities.resolutions.length > 0 && (
                <div>
                  <span>Resolutions</span>
                  <span className="mono">{provider.capabilities.resolutions.join(', ')}</span>
                </div>
              )}
            </div>
          </Panel>

          {provider.auth !== 'None' && <CredentialsForm provider={provider} />}

          <SessionPanel provider={provider} />

          <Panel title="What this connector is serving">
            {servingHere.length === 0 ? (
              <EmptyState>Nothing is routed to this connector right now.</EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Capability</th>
                      <th>Position in the chain</th>
                      <th>Source of this decision</th>
                    </tr>
                  </thead>
                  <tbody>
                    {servingHere.map((b) => (
                      <tr key={b.capability}>
                        <td>{b.capability}</td>
                        <td>
                          {b.providerKeys.indexOf(provider.key) === 0 ? (
                            <Badge tone="pos">primary</Badge>
                          ) : (
                            <Badge tone="neutral">
                              fallback #{b.providerKeys.indexOf(provider.key)}
                            </Badge>
                          )}
                        </td>
                        <td>
                          {b.isFallback ? (
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
          </Panel>
        </>
      )}
    </div>
  )
}
