/**
 * The pieces every AI page shares: a model's name, a call's outcome, an
 * agent's status, a chain in fallback order and its editor, and a link to a
 * call. Kept here so the five tabs say each thing the same way.
 */

import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  MAX_CHAIN,
  agentStatus,
  chainProblem,
  isEmbeddingModel,
  modelName,
  moveInChain,
  outcomeBadge,
  sameChain,
} from '../../lib/ai'
import type { AiModel } from '../../lib/ai'
import { Badge } from '../../components/ui'

/** "Nemotron 3 Ultra" with its full id beside it (or in its title when `bare`). */
export function ModelLabel({ id, bare }: { id: string | null | undefined; bare?: boolean }) {
  if (!id) return <span className="faint">none</span>
  if (bare) return <span title={id}>{modelName(id)}</span>
  return (
    <span className="ai-model-label">
      <span className="ai-model-label__name">{modelName(id)}</span>
      {modelName(id) !== id && <span className="ai-model-label__id mono">{id}</span>}
    </span>
  )
}

export function OutcomeBadge({ outcome }: { outcome: string | null | undefined }) {
  const b = outcomeBadge(outcome)
  return (
    <span className={`badge badge--${b.tone}`} title={b.means || undefined}>
      {b.label}
    </span>
  )
}

/** On / Off / Planned, with a dot; a planned agent says its phase. */
export function StatusPill({ status, phase }: { status: string; phase?: string }) {
  const s = agentStatus(status)
  const tone = s.tone === 'neutral' ? '' : `pill--${s.tone}`
  return (
    <span className={`pill ai-pill ${tone}`} title={s.means || undefined}>
      <span className="pill__dot" aria-hidden="true" />
      {status === 'planned' && phase ? `Planned · phase ${phase}` : s.label}
    </span>
  )
}

/** "call #41", to its detail on the Calls tab. */
export function CallLink({ id, children }: { id: number | null | undefined; children?: ReactNode }) {
  if (id == null) return null
  return (
    <Link className="ai-call-link" to={`/ai/calls?id=${id}`}>
      {children ?? `call #${id}`}
    </Link>
  )
}

/**
 * A chain in fallback order, as numbered chips: the first answers, the next
 * only when the one before it fails. Full ids are in each chip's title.
 */
export function ChainChips({ chain, empty = 'no models' }: { chain: readonly string[]; empty?: string }) {
  if (chain.length === 0) return <span className="faint">{empty}</span>
  return (
    <ol className="ai-chain" aria-label="Models in fallback order">
      {chain.map((m, i) => (
        <li key={`${m}-${i}`} className={`ai-chain__item ${i === 0 ? 'ai-chain__item--first' : ''}`} title={m}>
          <span className="ai-chain__n">{i + 1}</span>
          {modelName(m)}
        </li>
      ))}
    </ol>
  )
}

/**
 * Edits a chain: reorder, remove, add from the catalog (the models in use
 * first, then every other one the provider listed, by vendor), and go back
 * to the default. Saving is the caller's; this only holds the draft and says
 * why a draft cannot be saved.
 */
export function ChainEditor({
  chain,
  resetTo,
  resetLabel,
  catalog,
  catalogNote,
  embedTier = false,
  saving,
  error,
  onSave,
  onReset,
  onCancel,
}: {
  chain: readonly string[]
  /** What "reset" goes back to, shown so the choice is not blind. */
  resetTo: readonly string[]
  resetLabel: string
  catalog: readonly AiModel[] | undefined
  /** Said under the picker when the catalog could not be read. */
  catalogNote?: string | null
  embedTier?: boolean
  saving: boolean
  error: string | null
  onSave: (chain: string[], reason: string) => void
  onReset: (reason: string) => void
  onCancel: () => void
}) {
  const [draft, setDraft] = useState<string[]>([...chain])
  const [pick, setPick] = useState('')
  const [reason, setReason] = useState('')
  const problem = chainProblem(draft, embedTier)
  const unchanged = sameChain(draft, chain)

  // What can be added: the catalog's models of the right kind, not already in the draft.
  const options = useMemo(() => {
    const fits = (id: string) => isEmbeddingModel(id) === embedTier && !draft.includes(id)
    const models = (catalog ?? []).filter((m) => fits(m.id))
    const inUse = models.filter((m) => m.inUse)
    const byVendor = new Map<string, AiModel[]>()
    for (const m of models) {
      if (m.inUse) continue
      const v = m.ownedBy || 'other'
      byVendor.set(v, [...(byVendor.get(v) ?? []), m])
    }
    return { inUse, vendors: [...byVendor.entries()].sort(([a], [b]) => a.localeCompare(b)), count: models.length }
  }, [catalog, draft, embedTier])

  const add = () => {
    if (!pick || draft.includes(pick) || draft.length >= MAX_CHAIN) return
    setDraft([...draft, pick])
    setPick('')
  }

  return (
    <div className="ai-editor">
      <ol className="ai-editor__list">
        {draft.map((m, i) => (
          <li key={m} className="ai-editor__row">
            <span className="ai-chain__n">{i + 1}</span>
            <span className="ai-editor__model">
              <ModelLabel id={m} />
            </span>
            <span className="ai-editor__tools">
              <button type="button" className="btn btn--ghost btn--sm" disabled={i === 0} aria-label={`Move ${modelName(m)} up`} onClick={() => setDraft(moveInChain(draft, i, -1))}>
                ↑
              </button>
              <button
                type="button"
                className="btn btn--ghost btn--sm"
                disabled={i === draft.length - 1}
                aria-label={`Move ${modelName(m)} down`}
                onClick={() => setDraft(moveInChain(draft, i, 1))}
              >
                ↓
              </button>
              <button type="button" className="btn btn--ghost btn--sm" aria-label={`Remove ${modelName(m)}`} onClick={() => setDraft(draft.filter((x) => x !== m))}>
                ×
              </button>
            </span>
          </li>
        ))}
        {draft.length === 0 && <li className="ai-editor__row faint">No models: add one below.</li>}
      </ol>

      <div className="ai-editor__add">
        <select
          className="field__input field__input--sm ai-editor__pick"
          aria-label="Model to add"
          value={pick}
          disabled={draft.length >= MAX_CHAIN || options.count === 0}
          onChange={(e) => setPick(e.target.value)}
        >
          <option value="">
            {draft.length >= MAX_CHAIN
              ? `At most ${MAX_CHAIN} models`
              : catalog === undefined
                ? 'The catalog is not read yet'
                : `Add a model (${options.count} to pick from)`}
          </option>
          {options.inUse.length > 0 && (
            <optgroup label="In use on the desk">
              {options.inUse.map((m) => (
                <option key={m.id} value={m.id}>
                  {modelName(m.id)} · {m.id}
                </option>
              ))}
            </optgroup>
          )}
          {options.vendors.map(([vendor, models]) => (
            <optgroup key={vendor} label={vendor}>
              {models.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.id}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
        <button type="button" className="btn btn--sm" disabled={!pick} onClick={add}>
          Add
        </button>
      </div>
      {catalogNote && <p className="small-note warn ai-flush">{catalogNote}</p>}

      <input
        className="field__input field__input--sm ai-editor__reason"
        placeholder="Why (optional, kept with the change)"
        aria-label="Reason for the change"
        value={reason}
        maxLength={200}
        onChange={(e) => setReason(e.target.value)}
      />

      <div className="ai-editor__foot">
        <button type="button" className="btn btn--primary btn--sm" disabled={saving || unchanged || problem != null} onClick={() => onSave(draft, reason.trim())}>
          {saving ? 'Saving…' : 'Save chain'}
        </button>
        <button
          type="button"
          className="btn btn--ghost btn--sm"
          disabled={saving || sameChain(chain, resetTo)}
          title={`Back to ${resetTo.map(modelName).join(' → ')}`}
          onClick={() => onReset(reason.trim())}
        >
          {resetLabel}
        </button>
        <button type="button" className="btn btn--ghost btn--sm" disabled={saving} onClick={onCancel}>
          Cancel
        </button>
        {problem && !unchanged && <span className="small-note warn ai-flush">{problem}</span>}
      </div>
      <p className="small-note ai-flush">
        The first model answers; the next is asked only when the one before it fails or times out. {MAX_CHAIN} at most.
      </p>
      {error && (
        <div className="alert alert--error" role="alert">
          {error}
        </div>
      )}
    </div>
  )
}

/** "not known yet", said as such rather than as a zero. */
export function NotKnown({ children = 'not known yet' }: { children?: ReactNode }) {
  return <span className="faint">{children}</span>
}

/** A tone badge for "key configured". */
export function KeyBadge({ configured }: { configured: boolean }) {
  return <Badge tone={configured ? 'pos' : 'warn'}>{configured ? 'key configured' : 'no key on the server'}</Badge>
}
