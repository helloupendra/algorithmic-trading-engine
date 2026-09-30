/**
 * AI → Models (/ai/models): where the models come from and which ones the
 * desk uses.
 *
 * The provider's catalog as the API read it, and how fresh that is (fresh,
 * a cached copy, or unavailable with the reason). The tiers' chains in
 * fallback order, each editable and resettable to its default. The models in
 * use first, with the tiers and agents that use each, today's calls and the
 * last health test; then every other model the provider lists, by vendor,
 * searchable. Any chat model can be tested: one tiny question, logged in the
 * Calls tab like any other call. Last, the models the desk runs itself
 * (FinBERT), which are not language models the provider serves.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import {
  callTime,
  catalogGroups,
  catalogSourceText,
  formatSeconds,
  modelName,
  useAiAgents,
  useAiModels,
  useAiOverview,
  useTestModel,
  useUpdateTier,
} from '../../lib/ai'
import type { AiModel, AiModelsResponse, AiTier, ModelTestResult } from '../../lib/ai'
import { formatDateTime } from '../../lib/format'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'
import { IconChip, IconLayers, IconSearch, IconServer } from '../../components/icons'
import { CallLink, ChainChips, ChainEditor, KeyBadge } from './parts'
import { errorText, useNow } from './common'
import '../system/health/health.css'
import './ai.css'

/** A test's result as it stands on this page: asked, answered, or failed to ask. */
type TestState = { pending: true } | { pending: false; result: ModelTestResult } | { pending: false; error: string }

function TierRow({ tier, catalog, catalogNote }: { tier: AiTier; catalog: AiModel[] | undefined; catalogNote: string | null }) {
  const update = useUpdateTier()
  const [editing, setEditing] = useState(false)
  const done = { onSuccess: () => setEditing(false) }
  return (
    <div className="ai-tier ai-tier--edit">
      <div className="ai-tier__head">
        <b>{tier.label}</b>
        {tier.overridden ? (
          <Badge tone="warn">changed{tier.updatedBy ? ` by ${tier.updatedBy}` : ''}</Badge>
        ) : (
          <span className="faint ai-tier__default">default</span>
        )}
        <span className="ai-tier__purpose muted">{tier.purpose}</span>
        {tier.chat && !editing && (
          <button type="button" className="btn btn--ghost btn--sm ai-tier__edit" onClick={() => setEditing(true)}>
            Edit chain
          </button>
        )}
      </div>
      <ChainChips chain={tier.chain} />
      {tier.overridden && tier.updatedUtc && (
        <p className="small-note ai-flush">
          Changed {formatDateTime(tier.updatedUtc)} IST. The default is {tier.defaultChain.map(modelName).join(' → ')}.
        </p>
      )}
      {!tier.chat && <p className="small-note ai-flush">Makes vectors for search, not answers, so it is neither asked nor edited here.</p>}
      {editing && (
        <ChainEditor
          chain={tier.chain}
          resetTo={tier.defaultChain}
          resetLabel="Reset to the default"
          catalog={catalog}
          catalogNote={catalogNote}
          saving={update.isPending}
          error={update.isError ? errorText(update.error) : null}
          onSave={(chain, reason) => update.mutate({ tier: tier.key, chain, reason }, done)}
          onReset={(reason) => update.mutate({ tier: tier.key, chain: null, reason }, done)}
          onCancel={() => {
            update.reset()
            setEditing(false)
          }}
        />
      )}
    </div>
  )
}

function LastTest({ model, now }: { model: AiModel; now: number }) {
  const t = model.lastTest
  if (!t) return <span className="faint">never tested</span>
  return (
    <span>
      <span className={t.ok ? 'pos' : 'neg'}>{t.ok ? 'answered' : 'failed'}</span>
      {t.seconds != null && ` in ${formatSeconds(t.seconds)}`}{' '}
      <span className="faint" title={`${formatDateTime(t.utc)} IST`}>
        {callTime(t.utc, now)}
      </span>
      {t.callId != null && (
        <>
          {' · '}
          <CallLink id={t.callId} />
        </>
      )}
      {!t.ok && t.error && <span className="ai-model__err mono">{t.error}</span>}
    </span>
  )
}

function TestButton({ model, state, onTest }: { model: AiModel; state: TestState | undefined; onTest: (id: string) => void }) {
  if (model.embedding) {
    return (
      <span className="faint ai-model__notest" title="An embedding model makes vectors; it cannot be asked a question">
        not testable
      </span>
    )
  }
  return (
    <button type="button" className="btn btn--sm ai-model__test" disabled={state?.pending === true} onClick={() => onTest(model.id)}>
      {state?.pending ? 'Testing…' : 'Test'}
    </button>
  )
}

/** What the test just done on this page came back with. */
function TestResult({ state }: { state: TestState | undefined }) {
  if (!state || state.pending) return null
  if ('error' in state) return <p className="ai-model__result neg">Not asked: {state.error}</p>
  const r = state.result
  return r.ok ? (
    <p className="ai-model__result">
      <span className="pos">Answered</span> in {formatSeconds(r.seconds)}: <span className="mono">“{(r.answer ?? '').slice(0, 80)}”</span>
      {r.callId != null && (
        <>
          {' · '}
          <CallLink id={r.callId} />
        </>
      )}
    </p>
  ) : (
    <p className="ai-model__result neg">
      Failed{r.seconds != null ? ` after ${formatSeconds(r.seconds)}` : ''}: <span className="mono">{r.error ?? 'no reason given'}</span>
      {r.callId != null && (
        <>
          {' · '}
          <CallLink id={r.callId} />
        </>
      )}
    </p>
  )
}

function InUseRow({
  model,
  now,
  test,
  onTest,
  agentName,
}: {
  model: AiModel
  now: number
  test: TestState | undefined
  onTest: (id: string) => void
  agentName: (key: string) => string
}) {
  const d = model.today
  return (
    <div className="ai-model">
      <div className="ai-model__name">
        <b>{modelName(model.id)}</b>
        <span className="mono faint ai-model__id">{model.id}</span>
        {!model.listed && <Badge tone="warn">not in the provider's list</Badge>}
      </div>
      <div className="ai-model__use">
        {model.tiers.length > 0 && <span>{model.tiers.map((t) => t[0].toUpperCase() + t.slice(1)).join(', ')} tier</span>}
        {model.agents.length > 0 && <span className="muted"> · used by {model.agents.map(agentName).join(', ')}</span>}
        {model.note && <span className="faint ai-model__note">{model.note}</span>}
      </div>
      <div className="ai-model__today">
        {d ? (
          <>
            {d.calls} call{d.calls === 1 ? '' : 's'} today · {d.ok} ok · <span className={d.failed > 0 ? 'neg' : ''}>{d.failed} failed</span>
            {d.avgSeconds != null && ` · avg ${formatSeconds(d.avgSeconds)}`}
          </>
        ) : (
          <span className="faint">no calls today</span>
        )}
      </div>
      <div className="ai-model__last">
        <LastTest model={model} now={now} />
      </div>
      <div className="ai-model__act">
        <TestButton model={model} state={test} onTest={onTest} />
      </div>
      <div className="ai-model__resultrow">
        <TestResult state={test} />
        {!model.listed && (
          <p className="small-note warn ai-flush">
            The provider's list does not include this model any more: calls to it will likely fail. Pick another in its tier's
            chain.
          </p>
        )}
      </div>
    </div>
  )
}

function CatalogRow({ model, now, test, onTest }: { model: AiModel; now: number; test: TestState | undefined; onTest: (id: string) => void }) {
  return (
    <div className="ai-cat">
      <span className="mono ai-cat__id">{model.id}</span>
      <span className="ai-cat__last">
        <LastTest model={model} now={now} />
      </span>
      <span className="ai-cat__act">
        <TestButton model={model} state={test} onTest={onTest} />
      </span>
      {test && !test.pending && (
        <div className="ai-cat__result">
          <TestResult state={test} />
        </div>
      )}
    </div>
  )
}

function CatalogLine({ data }: { data: AiModelsResponse }) {
  const src = catalogSourceText(data.source)
  return (
    <p className="ai-catline">
      <Badge tone={src.tone === 'pos' ? 'pos' : src.tone === 'warn' ? 'warn' : 'neutral'}>{src.label}</Badge>
      <span className="muted">
        {data.models.length} model{data.models.length === 1 ? '' : 's'}
        {data.fetchedUtc && ` · read ${formatDateTime(data.fetchedUtc)} IST`}
      </span>
      {data.error && <span className="warn ai-catline__err">{data.error}</span>}
    </p>
  )
}

export function AiModelsPage() {
  const models = useAiModels()
  const overview = useAiOverview()
  const agents = useAiAgents()
  const test = useTestModel()
  const [tests, setTests] = useState<Record<string, TestState>>({})
  const [query, setQuery] = useState('')
  const now = useNow(30_000)
  const data = models.data
  const provider = overview.data?.provider

  const onTest = (id: string) => {
    setTests((t) => ({ ...t, [id]: { pending: true } }))
    test.mutate(id, {
      onSuccess: (result) => setTests((t) => ({ ...t, [id]: { pending: false, result } })),
      onError: (error) => setTests((t) => ({ ...t, [id]: { pending: false, error: errorText(error) } })),
    })
  }

  const catalogNote = models.isError
    ? `The catalog could not be read (${errorText(models.error)}).`
    : data?.source === 'unavailable'
      ? `The provider's list is unavailable${data.error ? ` (${data.error})` : ''}: only the models the tiers name can be picked.`
      : null
  const groups = data ? catalogGroups(data.models, query) : null
  const agentName = (key: string) => agents.data?.agents.find((a) => a.key === key)?.name ?? key
  const searching = query.trim() !== ''

  return (
    <div className="page hp ai">
      <div className="hp-bar">
        <p className="hp-bar__lead muted">
          The models the desk can call, from the provider's catalog, the ones in use first. A test asks one tiny question and is
          logged in the Calls tab like any other call.
        </p>
      </div>

      <section className="ai-provider" aria-label="Provider">
        <div className="ai-provider__line">
          <IconServer className="ai-provider__icon" />
          {provider ? (
            <>
              <b>{provider.name}</b>
              <span className="mono ai-provider__url">{provider.baseUrl}</span>
              <KeyBadge configured={provider.keyConfigured} />
            </>
          ) : overview.isError ? (
            <span className="warn">The provider could not be read: {errorText(overview.error)}</span>
          ) : (
            <span className="faint">Provider not known yet</span>
          )}
        </div>
        {data && <CatalogLine data={data} />}
      </section>

      {overview.data && (
        <Panel
          title={
            <>
              <IconLayers /> Tier chains <span className="faint ai-title-note">fallback order</span>
            </>
          }
        >
          <div className="ai-tiers">
            {overview.data.tiers.map((t) => (
              <TierRow key={t.key} tier={t} catalog={data?.models} catalogNote={catalogNote} />
            ))}
          </div>
          <p className="small-note ai-flush">
            An agent uses its tier's chain unless it has one of its own (<Link to="/ai/agents">Agents</Link>). A changed chain
            applies from the next call.
          </p>
        </Panel>
      )}

      {models.isPending ? (
        <Loading label="Reading the catalog…" />
      ) : !data ? (
        <>
          <InlineError error={models.error} />
          <p className="small-note ai-flush">The catalog comes from GET /api/Ai/models; until it answers, which models exist is not known.</p>
        </>
      ) : (
        <>
          <Panel
            title={
              <>
                <IconChip /> In use <span className="faint ai-title-note">{groups!.inUse.length}</span>
              </>
            }
            actions={
              <label className="ai-search">
                <IconSearch aria-hidden="true" />
                <input
                  className="field__input field__input--sm"
                  type="search"
                  placeholder="Search models…"
                  aria-label="Search models"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                />
              </label>
            }
          >
            {groups!.inUse.length === 0 ? (
              <EmptyState>{searching ? 'No model in use matches.' : 'No model is in use.'}</EmptyState>
            ) : (
              <div className="ai-models">
                {groups!.inUse.map((m) => (
                  <InUseRow key={m.id} model={m} now={now} test={tests[m.id]} onTest={onTest} agentName={agentName} />
                ))}
              </div>
            )}
          </Panel>

          <Panel
            title={
              <>
                The rest of the catalog{' '}
                <span className="faint ai-title-note">
                  {groups!.vendors.reduce((n, g) => n + g.models.length, 0)} models · {groups!.vendors.length} vendors
                </span>
              </>
            }
          >
            {data.source === 'unavailable' ? (
              <p className="small-note warn ai-flush">
                The provider's list could not be read{data.error ? `: ${data.error}` : ''}. Only the models the tiers name are known
                until it can.
              </p>
            ) : groups!.vendors.length === 0 ? (
              <EmptyState>{searching ? 'Nothing else matches.' : 'The provider listed nothing else.'}</EmptyState>
            ) : (
              <div className="ai-vendors">
                {groups!.vendors.map((g) => (
                  <details key={`${g.vendor}-${searching}`} className="ai-vendor" open={searching}>
                    <summary className="ai-vendor__summary">
                      <span className="ai-vendor__name">{g.vendor}</span> <span className="faint">{g.models.length}</span>
                    </summary>
                    <div className="ai-vendor__list">
                      {g.models.map((m) => (
                        <CatalogRow key={m.id} model={m} now={now} test={tests[m.id]} onTest={onTest} />
                      ))}
                    </div>
                  </details>
                ))}
              </div>
            )}
          </Panel>

          <Panel
            title={
              <>
                <IconServer /> Local models <span className="faint ai-title-note">run on the desk's own server</span>
              </>
            }
          >
            {data.local.length === 0 ? (
              <EmptyState>None listed.</EmptyState>
            ) : (
              <div className="ai-rules__list">
                {data.local.map((l) => (
                  <div key={l.id} className="ai-rule">
                    <span className="ai-rule__name mono">{l.id}</span>
                    <span className="ai-rule__what">{l.usedBy}</span>
                    <span className="ai-rule__where faint">{l.where}</span>
                    <span className="ai-rule__model muted">{l.kind}</span>
                  </div>
                ))}
              </div>
            )}
          </Panel>
        </>
      )}
    </div>
  )
}
