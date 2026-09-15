/**
 * Strategies module — the Library page's cards. One card is at most four
 * lines: the name, a one-line description, a few chips (legs, timeframe,
 * data, exit) and a foot with the run state, the recent paper P&L and the two
 * actions. A family of variants is one card that opens into compact rows.
 *
 * Everything shown is derived in lib/strategyLibrary.ts from the catalog
 * entry, the spec's facts and the run history; a fact that is not known is
 * left out rather than shown as a default.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { formatInrSigned, pnlClass } from '../../lib/format'
import { activeUnderlyings } from '../../lib/strategyList'
import {
  builtInExit,
  dataLabel,
  oneLiner,
  shortLegs,
  sumRecentPaper,
  timeframeLabel,
} from '../../lib/strategyLibrary'
import type { RecentPaper } from '../../lib/strategyLibrary'
import type { SpecFacts } from '../../lib/specs'
import type { StrategyListItem } from '../../lib/types'
import { IconChevronDown, IconChevronRight, IconPlay } from '../../components/icons'

export interface CardContext {
  facts: ReadonlyMap<number, SpecFacts>
  recent: ReadonlyMap<number, RecentPaper> | null
  /** Days the recent P&L covers, for its label. */
  recentDays: number
  onStart: (strategy: StrategyListItem) => void
}

const specHref = (s: StrategyListItem) => `/admin/strategies/library/${s.id}`

/* ------------------------------------------------------------------ chips */

function Chips({ s, facts, compact = false }: { s: StrategyListItem; facts: SpecFacts | undefined; compact?: boolean }) {
  const legs = shortLegs(s.legsSummary)
  const timeframe = timeframeLabel(facts, s.dataRequirements)
  const data = dataLabel(facts, s.dataRequirements)
  const exit = builtInExit(facts)
  return (
    <div className="lib-chips">
      {legs ? (
        <span className="lib-chip lib-chip--legs" title={s.legsSummary}>
          {legs}
        </span>
      ) : (
        <span className="lib-chip">Alerts only</span>
      )}
      {!compact && timeframe && <span className="lib-chip">{timeframe}</span>}
      {!compact && data && (
        <span className={`lib-chip${data === 'Chain OI' ? ' lib-chip--strong' : ''}`} title={facts?.data ? `Data: ${facts.data}` : undefined}>
          {data}
        </span>
      )}
      {exit != null && legs && (
        <span
          className={`lib-chip${exit ? '' : ' lib-chip--quiet'}`}
          title={
            exit
              ? 'Closes positions by its own rules; the run’s risk rules still apply'
              : 'No exit of its own: holds until the run’s risk rules, a manual stop or the platform’s end-of-session square-off'
          }
        >
          {exit ? 'Own exit' : 'No own exit'}
        </span>
      )}
    </div>
  )
}

/* ------------------------------------------------------------ foot pieces */

function RunningNote({ on }: { on: string[] }) {
  if (on.length === 0) return null
  return (
    <span className="lib-live" title={`Live on ${on.join(', ')}`}>
      <span className="lib-live__dot" aria-hidden="true" />
      Running on {on.join(', ')}
    </span>
  )
}

function RecentNote({ recent, days }: { recent: RecentPaper | undefined; days: number }) {
  if (!recent || recent.runs + recent.alertRuns === 0) {
    return <span className="lib-recent lib-recent--none">No runs in {days}d</span>
  }
  const total = recent.runs + recent.alertRuns
  return (
    <span className="lib-recent" title={`Paper runs started in the last ${days} days, every user`}>
      {recent.runs > 0 ? (
        <>
          {days}d paper <span className={`mono ${pnlClass(recent.netPnl)}`}>{formatInrSigned(recent.netPnl)}</span>
          <span className="lib-recent__runs"> · {total} {total === 1 ? 'run' : 'runs'}</span>
        </>
      ) : (
        <>
          {days}d · {recent.alertRuns} alert {recent.alertRuns === 1 ? 'run' : 'runs'}
        </>
      )}
    </span>
  )
}

function Actions({ s, onStart }: { s: StrategyListItem; onStart: (s: StrategyListItem) => void }) {
  const on = activeUnderlyings(s)
  return (
    <div className="lib-actions">
      <Link className="btn btn--sm btn--ghost" to={specHref(s)} title="Entry, exits, data it needs and a worked example">
        How it works
      </Link>
      <button
        type="button"
        className="btn btn--sm"
        onClick={() => onStart(s)}
        title={on.length > 0 ? `Already running on ${on.join(', ')} — start it on another underlying` : `Start ${s.name} on paper`}
        aria-label={`Start ${s.name}`}
      >
        <IconPlay style={{ width: 12, height: 12 }} /> Start
      </button>
    </div>
  )
}

/* ------------------------------------------------------------ single card */

export function LibraryCard({ strategy: s, ctx }: { strategy: StrategyListItem; ctx: CardContext }) {
  const on = activeUnderlyings(s)
  const line = oneLiner(s.description)
  return (
    <article className={`lib-card${on.length > 0 ? ' lib-card--running' : ''}`}>
      <div className="lib-card__head">
        <Link className="lib-card__name" to={specHref(s)}>
          {s.name}
        </Link>
        {s.category && <span className="lib-card__cat">{s.category}</span>}
      </div>
      <p className="lib-card__line" title={s.description || undefined}>
        {line || <span className="faint">No description provided by the strategy.</span>}
      </p>
      <Chips s={s} facts={ctx.facts.get(s.id)} />
      <div className="lib-card__foot">
        <div className="lib-card__state">
          <RunningNote on={on} />
          {ctx.recent && <RecentNote recent={ctx.recent.get(s.id)} days={ctx.recentDays} />}
        </div>
        <Actions s={s} onStart={ctx.onStart} />
      </div>
    </article>
  )
}

/* ------------------------------------------------------------ family card */

/**
 * Variants of one strategy in one section, as a single card. Its line is the
 * simplest variant's (the shortest name — the family's plain form), named so
 * it is not read as every variant's rule. Opens into a row per variant.
 * `open` is forced while a search or filter is narrowing the list.
 */
export function FamilyCard({
  root,
  members,
  ctx,
  forceOpen = false,
}: {
  root: string
  members: StrategyListItem[]
  ctx: CardContext
  forceOpen?: boolean
}) {
  const [openState, setOpen] = useState(false)
  const open = forceOpen || openState
  const base = [...members].sort((a, b) => a.name.length - b.name.length || a.name.localeCompare(b.name))[0]
  const running = [...new Set(members.flatMap(activeUnderlyings))]
  const runningMembers = members.filter((m) => m.activeRuns.length > 0).length
  const recent = ctx.recent ? sumRecentPaper(members.map((m) => ctx.recent!.get(m.id))) : undefined
  const categories = [...new Set(members.map((m) => m.category).filter(Boolean))]
  const listId = `family-${root}-${members[0].id}`

  return (
    <article className={`lib-card lib-card--family${runningMembers > 0 ? ' lib-card--running' : ''}${open ? ' is-open' : ''}`}>
      <div className="lib-card__head">
        <span className="lib-card__name lib-card__name--plain">{root} variants</span>
        <span className="lib-card__count">{members.length}</span>
        {categories.length === 1 && <span className="lib-card__cat">{categories[0]}</span>}
      </div>
      <p className="lib-card__line" title={base.description || undefined}>
        <span className="faint">{base.name}: </span>
        {oneLiner(base.description, 100) || 'no description'}
      </p>
      <Chips s={base} facts={ctx.facts.get(base.id)} />
      <div className="lib-card__foot">
        <div className="lib-card__state">
          {runningMembers > 0 && (
            <span className="lib-live">
              <span className="lib-live__dot" aria-hidden="true" />
              {runningMembers === 1 ? '1 variant' : `${runningMembers} variants`} running · {running.join(', ')}
            </span>
          )}
          {ctx.recent && <RecentNote recent={recent} days={ctx.recentDays} />}
        </div>
        <button
          type="button"
          className="btn btn--sm btn--ghost lib-family__toggle"
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          aria-controls={listId}
          disabled={forceOpen}
        >
          {open ? <IconChevronDown style={{ width: 13, height: 13 }} /> : <IconChevronRight style={{ width: 13, height: 13 }} />}
          {open ? 'Hide variants' : `Show ${members.length} variants`}
        </button>
      </div>

      {open && (
        <ul className="lib-variants" id={listId}>
          {members.map((m) => {
            const on = activeUnderlyings(m)
            return (
              <li key={m.id} className="lib-variant">
                <div className="lib-variant__main">
                  <Link className="lib-variant__name" to={specHref(m)} title={m.description || undefined}>
                    {m.name}
                  </Link>
                  <Chips s={m} facts={ctx.facts.get(m.id)} compact />
                </div>
                <div className="lib-variant__side">
                  <RunningNote on={on} />
                  {ctx.recent && <RecentNote recent={ctx.recent.get(m.id)} days={ctx.recentDays} />}
                  <Actions s={m} onStart={ctx.onStart} />
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </article>
  )
}
