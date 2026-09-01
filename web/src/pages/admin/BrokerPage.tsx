/**
 * FYERS broker connection, driven entirely from the console:
 *
 *   Connect → we fetch the hosted-login URL from the API and send the browser
 *   to FYERS → FYERS redirects to the API callback, which saves the token and
 *   bounces the browser straight back here with ?connected=1|0.
 *
 * Tokens expire daily, so this is the first stop of every trading morning.
 */

import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import {
  useBrokerConfig,
  useBrokerSession,
  useIngestorStatuses,
  useSaveBrokerConfig,
} from '../../lib/queries'
import { formatAge, formatDateTime } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'

export function BrokerPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const session = useBrokerSession()
  const config = useBrokerConfig()
  const saveConfig = useSaveBrokerConfig()
  const ingestors = useIngestorStatuses()
  const qc = useQueryClient()

  const [connectError, setConnectError] = useState<unknown>(null)
  const [form, setForm] = useState({ clientId: '', secretKey: '', redirectUri: '' })

  // Pre-fill the form from whatever the server already knows (config or DB);
  // the secret is never returned, so that field always starts empty.
  const cfg = config.data
  useEffect(() => {
    if (!cfg) return
    setForm((f) => ({
      clientId: f.clientId || cfg.clientId,
      secretKey: f.secretKey,
      redirectUri: f.redirectUri || cfg.redirectUri || cfg.suggestedRedirectUri,
    }))
  }, [cfg])

  const credsMissing = cfg?.source === 'none'

  const connect = useMutation({
    mutationFn: () => api.get<{ authUrl: string }>('/api/Auth/url'),
    onSuccess: ({ authUrl }) => {
      // Leave the SPA: FYERS hosted login → API callback → back to this page.
      window.location.assign(authUrl)
    },
    onError: setConnectError,
  })

  const disconnect = useMutation({
    mutationFn: () => api.post<{ message: string }>('/api/Auth/logout'),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['broker'] }),
  })

  const connected = searchParams.get('connected')
  const reason = searchParams.get('reason')

  function dismissBanner() {
    setSearchParams({}, { replace: true })
  }

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Broker connection</h1>
        <p className="page__subtitle">
          FYERS OAuth session for market data and (later) order placement. Tokens expire
          daily around the market's morning cutoff — reconnect each trading day.
        </p>
      </header>

      {connected === '1' && (
        <div className="alert alert--success" role="status">
          FYERS connected — the token is saved. You can start the ingestor now.
          <button type="button" className="btn btn--ghost btn--sm" onClick={dismissBanner}>
            Dismiss
          </button>
        </div>
      )}
      {connected === '0' && (
        <div className="alert alert--error" role="alert">
          FYERS connection failed{reason ? `: ${reason}` : '.'}
          <button type="button" className="btn btn--ghost btn--sm" onClick={dismissBanner}>
            Dismiss
          </button>
        </div>
      )}
      {connectError != null && <InlineError error={connectError} />}
      {disconnect.isError && <InlineError error={disconnect.error} />}

      <Panel title="FYERS app credentials">
        <p className="muted" style={{ maxWidth: '78ch' }}>
          Each installation uses its <b>own</b> FYERS app. Create one at{' '}
          <a href="https://myapi.fyers.in" target="_blank" rel="noreferrer noopener">myapi.fyers.in</a>{' '}
          with the redirect URL below, then save the App ID and secret here — they are stored
          encrypted in this installation's database, never in the repository.
        </p>
        {config.data && (
          <p className="small-note muted">
            Current source:{' '}
            <Badge tone={config.data.source === 'database' ? 'pos' : config.data.source === 'config' ? 'accent' : 'warn'}>
              {config.data.source === 'database'
                ? `saved in database by ${config.data.updatedBy ?? 'admin'}`
                : config.data.source === 'config'
                  ? 'server configuration (.env fallback)'
                  : 'not configured'}
            </Badge>
          </p>
        )}
        {saveConfig.isError && <InlineError error={saveConfig.error} />}
        {saveConfig.isSuccess && (
          <div className="alert alert--success" role="status">Credentials saved — you can connect now.</div>
        )}
        <form
          className="form-row"
          onSubmit={(e) => {
            e.preventDefault()
            saveConfig.mutate(form, { onSuccess: () => setForm((f) => ({ ...f, secretKey: '' })) })
          }}
        >
          <div className="field">
            <label className="field__label" htmlFor="bc-client">App ID (client id)</label>
            <input
              id="bc-client"
              className="field__input"
              required
              placeholder="XXXXXXXXXX-100"
              value={form.clientId}
              onChange={(e) => setForm({ ...form, clientId: e.target.value })}
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="bc-secret">Secret key</label>
            <input
              id="bc-secret"
              className="field__input"
              type="password"
              required
              placeholder={config.data?.hasSecret ? 'saved — re-enter to change' : 'from myapi.fyers.in'}
              value={form.secretKey}
              onChange={(e) => setForm({ ...form, secretKey: e.target.value })}
              autoComplete="new-password"
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="bc-redirect">Redirect URL (register this with FYERS)</label>
            <input
              id="bc-redirect"
              className="field__input"
              required
              value={form.redirectUri}
              onChange={(e) => setForm({ ...form, redirectUri: e.target.value })}
            />
          </div>
          <button className="btn btn--primary" disabled={saveConfig.isPending}>
            {saveConfig.isPending ? 'Saving…' : 'Save credentials'}
          </button>
        </form>
      </Panel>

      <Panel title="Session">
        <QueryBoundary query={session}>
          {(s) => (
            <div className="broker-state">
              <div>
                <div className={`killswitch__state ${s.isAuthenticated ? 'pos' : 'warn'}`}>
                  {s.isAuthenticated ? 'Connected' : 'Not connected'}
                </div>
                <p className="muted">
                  {s.isAuthenticated
                    ? `Token saved ${formatAge(s.updatedUtc ?? s.createdUtc)} (${formatDateTime(s.updatedUtc ?? s.createdUtc)}). FYERS tokens expire daily — reconnect tomorrow morning.`
                    : 'No usable FYERS token. Connect to authorize this platform against your FYERS account.'}
                </p>
              </div>
              <div className="broker-state__actions">
                <button
                  type="button"
                  className="btn btn--primary"
                  disabled={connect.isPending || credsMissing}
                  title={credsMissing ? 'Save your FYERS app credentials above first' : undefined}
                  onClick={() => {
                    setConnectError(null)
                    connect.mutate()
                  }}
                >
                  {connect.isPending
                    ? 'Redirecting to FYERS…'
                    : s.isAuthenticated
                      ? 'Reconnect FYERS'
                      : 'Connect FYERS'}
                </button>
                {s.isAuthenticated && (
                  <button
                    type="button"
                    className="btn btn--ghost"
                    disabled={disconnect.isPending}
                    onClick={() => disconnect.mutate()}
                  >
                    Disconnect
                  </button>
                )}
              </div>
            </div>
          )}
        </QueryBoundary>
        <p className="muted small-note">
          Connect opens FYERS's own login page — this platform never sees your FYERS
          password, only the OAuth token FYERS issues. The callback URL registered with
          FYERS must match the API's <code>Fyers:RedirectUri</code>.
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
            <b>Watch it come alive</b> — the ingestor heartbeat below should turn healthy
            within a minute, and Watchlist quotes start updating.
          </li>
          <li>
            <b>Optional:</b> run a daily-candle backfill under Data ingestion to fill
            historical charts.
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
