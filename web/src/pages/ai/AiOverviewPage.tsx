/**
 * AI → Overview (/ai): the desk's AI on one sheet.
 *
 * Where the models come from (the provider, its base URL, whether the
 * server holds a key, the free tier's terms), the tiers with their chains in
 * fallback order, what the AI did today (calls, outcomes, tokens, time,
 * fallbacks, per model), the rate limits as they stand, the last answer and
 * the last error, and every agent with its status. Each part links to the tab
 * that has it in full.
 *
 * Reads only. The key itself is never shown or asked for: the API says only
 * whether the server has one.
 */

import { Link } from 'react-router-dom'
import { formatSeconds, formatTokens, callTime, healthByModel, modelName } from '../../lib/ai'
import type { AiAgent, AiLimits, AiModelHealth, AiOverview, AiTier } from '../../lib/ai'
import { useAiAgents, useAiOverview } from '../../lib/ai'
import { formatAge, formatDateTime, formatTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { IconChip, IconClock, IconLayers, IconPulse, IconUsers } from '../../components/icons'
import { CallLink, ChainChips, HealthLine, KeyBadge, ModelLabel, StatusPill } from './parts'
import { errorText, useNow } from './common'
import '../system/health/health.css'
import './ai.css'

function ProviderPanel({ o }: { o: AiOverview }) {
  const p = o.provider
  return (
    <section className="ai-provider" aria-label="Provider">
      <div className="ai-provider__line">
        <b>{p.name}</b>
        <span className="mono ai-provider__url">{p.baseUrl}</span>
        <KeyBadge configured={p.keyConfigured} />
        {p.catalogUrl && (
          <a className="ai-provider__link" href={p.catalogUrl} target="_blank" rel="noreferrer noopener">
            Provider's catalog ↗
          </a>
        )}
      </div>
      {!p.keyConfigured && (
        <div className="alert alert--warn ai-flush" role="status">
          No NVIDIA key on the server, so no model can be asked. The key goes in the server's <code>.env</code> as{' '}
          <code>NVIDIA_API_KEY</code>; the desk regenerates its settings on the next deploy. It is never entered here.
        </div>
      )}
      {p.terms && <p className="small-note ai-flush">Terms: {p.terms}</p>}
    </section>
  )
}

function TodayTiles({ o }: { o: AiOverview }) {
  const t = o.today
  return (
    <div className="stat-grid stat-grid--pair ai-tiles">
      <StatTile
        label="Calls today"
        value={formatTokens(t.calls)}
        sub={
          <>
            {t.ok} ok · <span className={t.failed > 0 ? 'neg' : ''}>{t.failed} failed</span> ·{' '}
            <span className={t.refused > 0 ? 'warn' : ''}>{t.refused} refused</span>
            {t.cancelled > 0 && ` · ${t.cancelled} cancelled`}
          </>
        }
        to="/ai/calls"
      />
      <StatTile
        label="Tokens today"
        value={formatTokens(t.totalTokens)}
        sub={`${formatTokens(t.promptTokens)} in · ${formatTokens(t.completionTokens)} out`}
      />
      <StatTile
        label="Time per call"
        value={t.avgSeconds == null ? '—' : formatSeconds(t.avgSeconds)}
        sub={t.avgSeconds == null ? 'no answered call today' : `average · p95 ${formatSeconds(t.p95Seconds)}`}
      />
      <StatTile
        label="Fallbacks today"
        value={formatTokens(t.fallbacks)}
        tone={t.fallbacks > 0 ? 'warn' : undefined}
        sub="a model failed and the next one was asked"
      />
      <StatTile
        label="Running now"
        value={t.running == null ? '—' : formatTokens(t.running)}
        tone={t.running > 0 ? 'accent' : undefined}
        sub={`at most ${o.limits.maxConcurrent} at once`}
      />
    </div>
  )
}

function TiersPanel({ tiers, health, now }: { tiers: AiTier[]; health: AiModelHealth[]; now: number }) {
  const byModel = healthByModel(health)
  // The models not answering well, said in full under the chains: the chips only have room for a dot.
  const troubled = health.filter((h) => h.state === 'cooling' || h.state === 'failed')
  return (
    <Panel
      title={
        <>
          <IconLayers /> Tiers <span className="faint ai-title-note">fallback order</span>
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/models">
          Change →
        </Link>
      }
    >
      {tiers.length === 0 ? (
        <EmptyState>The API listed no tiers.</EmptyState>
      ) : (
        <div className="ai-tiers">
          {tiers.map((t) => (
            <div key={t.key} className="ai-tier">
              <div className="ai-tier__head">
                <b>{t.label}</b>
                {t.overridden && (
                  <Badge tone="warn">
                    changed{t.updatedBy ? ` by ${t.updatedBy}` : ''}
                  </Badge>
                )}
                <span className="ai-tier__purpose muted">{t.purpose}</span>
              </div>
              <ChainChips chain={t.chain} health={t.chat ? byModel : undefined} now={now} />
            </div>
          ))}
        </div>
      )}
      {troubled.length > 0 && (
        <ul className="ai-troubled">
          {troubled.map((h) => (
            <li key={h.model}>
              <b title={h.model}>{modelName(h.model)}</b> <HealthLine health={h} now={now} />
            </li>
          ))}
        </ul>
      )}
      <p className="small-note ai-flush">
        The first model answers; the next is asked only when the one before it fails or times out. A model that failed after a
        long wait, or twice running, cools for 10 to 60 minutes: it is asked after the healthy ones, never dropped, and one answer
        heals it.
        {health.length === 0 && ' This API build does not report model health yet.'}
      </p>
    </Panel>
  )
}

function Meter({ label, used, cap, sub }: { label: string; used: number; cap: number; sub: string }) {
  const pct = cap > 0 ? Math.min(100, (100 * used) / cap) : 0
  const tone = pct >= 90 ? 'neg' : pct >= 70 ? 'warn' : undefined
  return (
    <div className="metric ai-meter">
      <div className="metric__label">{label}</div>
      <div className={`metric__value ${tone ?? ''}`}>
        {used} <span className="faint">/ {cap}</span>
      </div>
      <div className="metric__sub">{sub}</div>
      <div className="progress" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={cap} aria-valuenow={used}>
        <div className={`progress__bar ${tone ? `progress__bar--${tone}` : ''}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

function LimitsPanel({ limits }: { limits: AiLimits }) {
  return (
    <Panel
      title={
        <>
          <IconPulse /> Rate limits
        </>
      }
    >
      <div className="ai-meters">
        <Meter label="Last minute, desk-wide" used={limits.usedLastMinute} cap={limits.globalPerMinute} sub="calls to the provider" />
        <Meter label="In flight" used={limits.inFlight} cap={limits.maxConcurrent} sub="at once, at most" />
      </div>
      <p className="small-note ai-flush">
        Each person may ask {limits.perUserPer10Min} questions in 10 minutes. {limits.providerNote}
      </p>
    </Panel>
  )
}

function LastPanel({ o, now }: { o: AiOverview; now: number }) {
  return (
    <Panel
      title={
        <>
          <IconClock /> Last answer, last error
        </>
      }
    >
      <div className="ai-last">
        <div className="ai-last__row">
          <span className="ai-last__label">Last answer</span>
          {o.lastOk ? (
            <span className="ai-last__what">
              <span title={`${formatDateTime(o.lastOk.utc)} IST`}>{callTime(o.lastOk.utc, now)}</span> ·{' '}
              <ModelLabel id={o.lastOk.model} bare /> · <CallLink id={o.lastOk.callId} />
            </span>
          ) : (
            <span className="faint">none recorded</span>
          )}
        </div>
        <div className="ai-last__row">
          <span className="ai-last__label">Last error</span>
          {o.lastError ? (
            <span className="ai-last__what">
              <span title={`${formatDateTime(o.lastError.utc)} IST`}>{callTime(o.lastError.utc, now)}</span> ·{' '}
              <CallLink id={o.lastError.callId} />
              <span className="ai-last__error mono">{o.lastError.error}</span>
            </span>
          ) : (
            <span className="faint">none recorded</span>
          )}
        </div>
      </div>
    </Panel>
  )
}

function ModelsToday({ o }: { o: AiOverview }) {
  return (
    <Panel
      title={
        <>
          <IconChip /> Models used today
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/models">
          Catalog →
        </Link>
      }
    >
      {o.byModel.length === 0 ? (
        <EmptyState>No model has been called today.</EmptyState>
      ) : (
        <div className="tablewrap">
          <table className="table ai-bymodel">
            <thead>
              <tr>
                <th>Model</th>
                <th className="num">Calls</th>
                <th className="num ai-hide-s">OK</th>
                <th className="num">Failed</th>
                <th className="num">Tokens</th>
                <th className="num ai-hide-s">Avg</th>
              </tr>
            </thead>
            <tbody>
              {o.byModel.map((m) => (
                <tr key={m.model}>
                  <td>
                    <ModelLabel id={m.model} bare />
                  </td>
                  <td className="num">
                    {m.calls}
                    <span className="ai-only-s faint"> calls</span>
                  </td>
                  <td className="num ai-hide-s">
                    {m.ok}
                    <span className="ai-only-s faint"> ok</span>
                  </td>
                  <td className={`num ${m.failed > 0 ? 'neg' : 'muted'}`}>
                    {m.failed}
                    <span className="ai-only-s faint"> failed</span>
                  </td>
                  <td className="num">
                    {formatTokens(m.totalTokens)}
                    <span className="ai-only-s faint"> tokens</span>
                  </td>
                  <td className="num ai-hide-s">
                    <span className="ai-only-s faint">avg </span>
                    {formatSeconds(m.avgSeconds)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function AgentGrid({ agents, counts }: { agents: AiAgent[] | undefined; counts: AiOverview['agents'] }) {
  return (
    <Panel
      title={
        <>
          <IconUsers /> Agents{' '}
          <span className="faint ai-title-note">
            {counts.total} · {counts.on} on · {counts.off} off · {counts.planned} planned
          </span>
        </>
      }
      actions={
        <Link className="btn btn--sm btn--ghost" to="/ai/agents">
          All agents →
        </Link>
      }
    >
      {!agents ? (
        <Loading label="Reading the agents…" />
      ) : (
        <div className="ai-minis">
          {agents.map((a) => (
            <Link key={a.key} to={`/ai/agents#agent-${a.key}`} className={`ai-mini ${a.status === 'planned' ? 'ai-mini--planned' : ''}`}>
              <span className="ai-mini__head">
                <span className="ai-mini__num">#{a.number}</span>
                <span className="ai-mini__name">{a.name}</span>
              </span>
              <span className="ai-mini__job">{a.job}</span>
              <span className="ai-mini__foot">
                <StatusPill status={a.status} phase={a.phase} />
                <span className="ai-mini__tier faint">
                  {a.tierLabel} · {modelName(a.chain[0])}
                </span>
              </span>
            </Link>
          ))}
        </div>
      )}
    </Panel>
  )
}

export function AiOverviewPage() {
  const overview = useAiOverview()
  const agents = useAiAgents()
  const now = useNow(15_000)
  const o = overview.data

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          Which models answer, where they come from, what each agent does, and what the AI did today. Admin-only; nothing
          here places or changes an order.
        </p>
        <span className="sys-asof">
          {o && (
            <span className="faint" title={formatDateTime(new Date(overview.dataUpdatedAt).toISOString())}>
              as of {formatTime(new Date(overview.dataUpdatedAt).toISOString())} · every 15 s
            </span>
          )}
          {overview.isError && o && <span className="warn">Refresh failed: showing the last reading ({formatAge(new Date(overview.dataUpdatedAt).toISOString())}).</span>}
        </span>
      </div>

      {overview.isPending ? (
        <Loading label="Reading the AI overview…" />
      ) : !o ? (
        <>
          <InlineError error={overview.error} />
          <p className="small-note ai-flush">
            The overview comes from GET /api/Ai/overview. If the API build predates the AI workspace, deploy the current
            one; nothing on this page is known until it answers.
          </p>
        </>
      ) : (
        <>
          <ProviderPanel o={o} />
          <TodayTiles o={o} />
          <div className="two-col ai-cols">
            <TiersPanel tiers={o.tiers} health={o.health} now={now} />
            <div className="stack-list">
              <LimitsPanel limits={o.limits} />
              <LastPanel o={o} now={now} />
            </div>
          </div>
          <ModelsToday o={o} />
          {agents.isError && !agents.data ? (
            <Panel title="Agents">
              <InlineError error={new Error(`The agents could not be read: ${errorText(agents.error)}`)} />
            </Panel>
          ) : (
            <AgentGrid agents={agents.data?.agents} counts={o.agents} />
          )}
        </>
      )}
    </div>
  )
}
