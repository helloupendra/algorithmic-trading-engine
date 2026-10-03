/**
 * Runs · net P&L: the object read all day. Strategy down the side,
 * underlying across, and in each cell one line per account (admin above,
 * coderforchange below, in the account colours), net of the charges of its
 * fills. Marks say what the number alone cannot: stopped by a risk rule,
 * restarted, a leg carried, or stopped and not restarted. Totals per account
 * at the bottom, per strategy on the right, with the trades and the charges
 * that turn a gross into a net.
 *
 * The columns are whatever underlyings ran today, however many: each takes
 * the width its figures need, and past what the panel can hold the table
 * scrolls sideways under a fixed Strategy column and a fixed Net column, so
 * the row is never read without its name or its answer. The panel's own width
 * (a container query in desk.css), not the window's, picks the short names;
 * the trades and charges columns and the "runs · lots" lines go by what the
 * panel can hold beside the underlyings, so four of them keep the columns in
 * a panel where eight would not. Under about 520px the same grid is a list, a
 * block per strategy with a line per underlying, because eight columns of
 * figures do not fit a phone whatever their width.
 *
 * Before the open the panel beside it is the morning plan: for the operator,
 * the plan file read against what is live (GET /api/Desk/plan); for a
 * trader, a tick per run of theirs deployed and waiting for its session.
 * Both are laid out by the same rules as the P&L grid (PlanSheet).
 */

import { Fragment, useCallback, useLayoutEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { CellMark, DeskAccount, GridCell, GridRow, PlanCell } from '../../lib/desk'
import { compactInr, istHm, underlyingShort } from '../../lib/desk'
import { formatInrSigned, formatInrWhole } from '../../lib/format'
import type { DeskLinks, DeskView } from './data'
import { toneClass, useDeskPlan } from './data'
import { Chip, Failed, PanelHead, Swatch, Waiting } from './parts'

const MARK_TONE: Record<CellMark['kind'], 'neg' | 'pos' | 'warn' | undefined> = {
  rule: 'neg',
  target: 'pos',
  restarted: undefined,
  stopped: 'warn',
  carried: 'warn',
}

function Marks({ marks }: { marks: CellMark[] }) {
  return (
    <>
      {marks.map((m) => (
        <Chip key={`${m.kind}${m.at ?? ''}`} tone={MARK_TONE[m.kind]} title={m.title}>
          {m.label}
          {m.at && <span className="dk-long">&nbsp;{m.at}</span>}
        </Chip>
      ))}
    </>
  )
}

/** One account's line in a cell: marks, the account's colour, the net (a link to the run). */
function CellLine({ cell, account, multi, links }: { cell: GridCell | null; account: DeskAccount; multi: boolean; links: DeskLinks }) {
  if (!cell) {
    return (
      <div className="dk-pc">
        <span className="dk-t3 dk-xs" title={`${account.name} does not run this here`}>
          —
        </span>
      </div>
    )
  }
  const f = cell.figures
  const last = cell.runs[cell.runs.length - 1]
  const title = `${account.name}: net ${formatInrSigned(f.net)} = gross ${formatInrSigned(f.gross)} − charges ${formatInrWhole(f.charges)} · ${f.trades} trade${f.trades === 1 ? '' : 's'}${f.open ? ` · open ${formatInrSigned(f.open)}` : ''}`
  const value = f.trades === 0 && f.open === 0 && f.net === 0 ? <span className="dk-t3">0</span> : compactInr(f.net)
  return (
    <div className="dk-pc">
      <Marks marks={cell.marks} />
      {multi && <Swatch tone={account.tone} cell />}
      <Link to={`${links.runBase}/${last.runId}`} className={toneClass(f.net)} title={title}>
        {value}
      </Link>
    </div>
  )
}

/** "6 runs · 2 lots · admin only": what the strategy's row says under its name. */
function rowDetail(row: GridRow, view: DeskView): string {
  const only = view.accounts.length > 1 && row.accountIds.length === 1 ? view.accounts.find((a) => a.id === row.accountIds[0])?.name : null
  return [`${row.runs} run${row.runs === 1 ? '' : 's'}`, row.lots != null ? `${row.lots} lot${row.lots === 1 ? '' : 's'}` : 'mixed lots', only ? `${only} only` : '']
    .filter(Boolean)
    .join(' · ')
}

function StrategyName({ row, view, tight }: { row: GridRow; view: DeskView; tight: boolean }) {
  return (
    <>
      <span className="dk-long">{row.label}</span>
      <span className="dk-short">{shortName(row.label)}</span>
      {!tight && <div className="dk-xs dk-t3">{rowDetail(row, view)}</div>}
    </>
  )
}

/**
 * What the table needs to keep its trades and charges columns and the
 * "runs · lots" lines beside the underlyings: the Strategy column's floor,
 * an underlying column's floor each and about 190px for the three figure
 * columns. The floors are the wide panel's (desk.css; a panel under 720px
 * sets smaller ones), kept on purpose as the margin for what runs wider than
 * a floor: a cell with marks, the "runs · lots" line under a short name.
 * What is still over then scrolls under the fixed columns.
 */
function fullWidth(underlyings: number): number {
  return 128 + 62 * underlyings + 190
}

/**
 * A scroll container's width, and whether its content is wider than it is,
 * kept current as either resizes: the table grows a column per underlying
 * that ran, and the panel shrinks with the window. Measured before the first
 * paint, so a narrow panel never shows the columns for a frame; 0 until then.
 */
function useOverflowX<T extends HTMLElement>(): [(el: T | null) => void, boolean, number] {
  const [el, setEl] = useState<T | null>(null)
  const [over, setOver] = useState(false)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    if (!el) return
    const check = () => {
      setOver(el.scrollWidth > el.clientWidth + 1)
      setWidth(el.clientWidth)
    }
    const observer = new ResizeObserver(check)
    observer.observe(el)
    if (el.firstElementChild) observer.observe(el.firstElementChild)
    check()
    return () => observer.disconnect()
  }, [el])
  return [useCallback((next: T | null) => setEl(next), []), over, width]
}

/** The account names once, over their columns of figures, in the same flex as the lines beneath. */
function ListColumns({ accounts }: { accounts: readonly DeskAccount[] }) {
  return (
    <div className="dk-gl__u dk-gl__cols">
      <span className="dk-gl__ul" />
      {accounts.map((a) => (
        <span key={a.id} className="dk-pc dk-xs dk-t3" title={a.name}>
          <Swatch tone={a.tone} cell />
          <span>{a.name}</span>
        </span>
      ))}
    </div>
  )
}

/**
 * The grid as a phone reads it: the account names once over their columns
 * (when more than one ran), then a block per strategy with its net on the right and a line per
 * underlying it ran with an account's figure per column, and the accounts'
 * totals in the same shape. Rendered beside the table; desk.css shows one
 * or the other by the panel's width.
 */
function GridList({ view, links, multi }: { view: DeskView; links: DeskLinks; multi: boolean }) {
  const grid = view.grid!
  return (
    <div className="dk-gl dk-gl--pnl" aria-label="Runs by strategy">
      {multi && <ListColumns accounts={grid.accounts} />}
      {grid.rows.map((row) => (
        <section className="dk-gl__row" key={row.strategy}>
          <div className="dk-gl__head">
            <span className="dk-gl__name">
              {row.label}
              <span className="dk-xs dk-t3 dk-gl__detail">{rowDetail(row, view)}</span>
            </span>
            <b className={`dk-n ${toneClass(row.figures.net)}`} title={formatInrSigned(row.figures.net)}>
              {compactInr(row.figures.net)}
            </b>
          </div>
          <div className="dk-gl__cells">
            {grid.underlyings
              .filter((u) => row.cells[u].some((c) => c != null))
              .map((u) => (
                <div className="dk-gl__u" key={u}>
                  <span className="dk-gl__ul dk-t3" title={u}>
                    {underlyingShort(u)}
                  </span>
                  {row.cells[u].map((c, i) => (
                    <CellLine key={grid.accounts[i].id} cell={c} account={grid.accounts[i]} multi={multi} links={links} />
                  ))}
                </div>
              ))}
          </div>
          <div className="dk-gl__meta dk-xs dk-t3">
            {row.figures.trades.toLocaleString('en-IN')} trade{row.figures.trades === 1 ? '' : 's'} · charges {compactInr(row.figures.charges, false)}
          </div>
        </section>
      ))}
      {grid.totals.map((t) => (
        <section className="dk-gl__row dk-gl__tot" key={t.account.id}>
          <div className="dk-gl__head">
            <span className="dk-gl__name dk-t2">
              {multi && <Swatch tone={t.account.tone} />}
              {multi ? t.account.name : 'Total'}
              <span className="dk-xs dk-t3 dk-gl__detail">{t.runs} runs</span>
            </span>
            <b className={`dk-n dk-net ${toneClass(t.figures.net)}`} title={formatInrSigned(t.figures.net)}>
              {compactInr(t.figures.net)}
            </b>
          </div>
          <div className="dk-gl__cells dk-gl__cells--tot">
            {grid.underlyings.map((u) => {
              const f = t.byUnderlying[u]
              if (!f) return null
              return (
                <span key={u} className="dk-n" title={`${u}: ${formatInrSigned(f.net)}`}>
                  <span className="dk-t3">{underlyingShort(u)}</span> <span className={toneClass(f.net)}>{compactInr(f.net)}</span>
                </span>
              )
            })}
          </div>
          <div className="dk-gl__meta dk-xs dk-t3">
            {t.figures.trades.toLocaleString('en-IN')} trades · charges {compactInr(t.figures.charges, false)}
          </div>
        </section>
      ))}
    </div>
  )
}

export function RunsGrid({ view, links }: { view: DeskView; links: DeskLinks }) {
  const meta = view.phase === 'post' ? 'final · net after charges' : 'net after charges · live'
  const head = <PanelHead title="Runs · net P&L" meta={meta} more={links.runs ? { to: links.runs, label: view.isAdmin ? 'Live runner' : 'My runs' } : null} />
  // Before any early return: a hook's place in the render must not move when the runs arrive.
  const [wrapRef, overflows, width] = useOverflowX<HTMLDivElement>()
  if (view.runsError && !view.grid) return <>{head}<Failed what="The runs" error={view.runsError} /></>
  if (!view.grid) return <>{head}<Waiting>Reading today’s runs…</Waiting></>
  const { grid } = view
  if (grid.rows.length === 0) {
    return (
      <>
        {head}
        <Waiting>{view.day === view.today ? 'No strategy runs today yet.' : 'No strategy runs on this day.'}</Waiting>
      </>
    )
  }
  const multi = grid.accounts.length > 1
  const anyMark = grid.rows.some((r) => Object.values(r.cells).some((cs) => cs.some((c) => c && c.marks.length)))
  // By the data, not the panel's width alone: a panel that holds four
  // underlyings with room to spare keeps the figure columns, one that cannot
  // hold eight gives them up and keeps the underlyings. Until measured, keep them.
  const tight = width > 0 && width < fullWidth(grid.underlyings.length)
  return (
    <>
      {head}
      <div className={`dk-gridwrap dk-gridwrap--pnl${overflows ? ' dk-gridwrap--scroll' : ''}`} ref={wrapRef}>
        <table className="dk-t dk-grid dk-grid--pnl">
          <thead>
            <tr>
              <th className="dk-fix dk-fix--l">Strategy</th>
              {grid.underlyings.map((u) => (
                <th key={u} className="r dk-u">
                  <span className="dk-long">{u}</span>
                  <span className="dk-short">{underlyingShort(u)}</span>
                </th>
              ))}
              {!tight && <th className="r">Trades</th>}
              {!tight && <th className="r">Charges</th>}
              <th className="r dk-fix dk-fix--r">Net</th>
            </tr>
          </thead>
          <tbody>
            {grid.rows.map((row) => (
              <tr key={row.strategy}>
                <td className="dk-sn dk-fix dk-fix--l" title={`${row.label} · ${rowDetail(row, view)}`}>
                  <StrategyName row={row} view={view} tight={tight} />
                </td>
                {grid.underlyings.map((u) => (
                  <td key={u} className="dk-u">
                    {row.cells[u].every((c) => c == null) ? null : row.cells[u].map((c, i) => <CellLine key={grid.accounts[i].id} cell={c} account={grid.accounts[i]} multi={multi} links={links} />)}
                  </td>
                ))}
                {!tight && <td className="r dk-n dk-t2">{row.figures.trades.toLocaleString('en-IN')}</td>}
                {!tight && (
                  <td className="r dk-n dk-t3" title={formatInrWhole(row.figures.charges)}>
                    {compactInr(row.figures.charges, false)}
                  </td>
                )}
                <td className="r dk-n dk-fix dk-fix--r">
                  <b className={toneClass(row.figures.net)} style={{ fontWeight: 500 }} title={formatInrSigned(row.figures.net)}>
                    {compactInr(row.figures.net)}
                  </b>
                </td>
              </tr>
            ))}
            {grid.totals.map((t, i) => (
              <tr key={t.account.id} className={`dk-tot${i === 0 ? ' dk-tot--first' : ''}`}>
                <td className="dk-fix dk-fix--l" title={`${t.account.name} · ${t.runs} runs`}>
                  {multi && <Swatch tone={t.account.tone} />}
                  <span className="dk-t2 dk-accname">{multi ? t.account.name : 'Total'}</span>
                  {!tight && <span className="dk-xs dk-t3"> {t.runs} runs</span>}
                </td>
                {grid.underlyings.map((u) => {
                  const f = t.byUnderlying[u]
                  return (
                    <td key={u} className={`r dk-n dk-u ${f ? toneClass(f.net) : ''}`} title={f ? formatInrSigned(f.net) : undefined}>
                      {f ? compactInr(f.net) : ''}
                    </td>
                  )
                })}
                {!tight && <td className="r dk-n dk-t2">{t.figures.trades.toLocaleString('en-IN')}</td>}
                {!tight && (
                  <td className="r dk-n dk-t3" title={formatInrWhole(t.figures.charges)}>
                    {compactInr(t.figures.charges, false)}
                  </td>
                )}
                <td className={`r dk-n dk-net dk-fix dk-fix--r ${toneClass(t.figures.net)}`} title={formatInrSigned(t.figures.net)}>
                  {compactInr(t.figures.net)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {overflows && <p className="dk-note dk-gridhint">Scroll sideways for the other underlyings; the strategy and its net stay put.</p>}
      <GridList view={view} links={links} multi={multi} />
      {(multi || anyMark) && (
        <div className="dk-foot">
          {multi && (
            <span>
              Each cell:{' '}
              {grid.accounts.map((a, i) => (
                <Fragment key={a.id}>
                  {i > 0 && ', '}
                  <Swatch tone={a.tone} />
                  {a.name}
                  {/* Where the account's line sits in a cell of the table; the list names the columns itself. */}
                  <span className="dk-tbl-only"> {i === 0 ? 'above' : i === grid.accounts.length - 1 ? 'below' : 'next'}</span>
                </Fragment>
              ))}
            </span>
          )}
          <span>
            <Chip>↻</Chip> restarted
          </span>
          <span>
            <Chip tone="warn">C</Chip> leg carried
          </span>
          <span>
            <Chip tone="neg">SL</Chip> stopped by a risk rule
          </span>
        </div>
      )}
    </>
  )
}

/** The plan is a narrow panel: a strategy by its first word ("SMC", "Ghost"), in full in the title. */
function shortName(label: string): string {
  return label.split(' ')[0]
}

/** The session each column opens with: MCX at 09:00, the rest at 09:15. */
function opensAt(u: string): string {
  return /^(CRUDEOIL|CRUDEOILM|NATURALGAS|GOLD|SILVER)/.test(u) ? '09:00' : '09:15'
}

/** "config/morning-plan.txt" out of the server's absolute path. */
function planFileName(path: string | null): string {
  if (!path) return 'the plan file'
  return path.split(/[\\/]/).filter(Boolean).slice(-2).join('/')
}

/**
 * What both plan grids draw: a strategy per row with a mark per account under
 * each underlying it is asked on, and for the operator a line per account
 * with its count. The same markup as the P&L grid: the Strategy column stays
 * put while the underlyings scroll, the panel's width picks the chip's word
 * ("not live" or "off"), and under about 380px (a phone: the plan's few narrow columns hold out
 * longer than the P&L grid's) it is a list, a block per strategy with a line
 * per underlying and the account names over their columns.
 */
interface PlanLine {
  key: string
  label: string
  title: string
  /** "2L", under the name. */
  sub: string | null
  /** Underlying → one mark per account, in the accounts' order, each a `.dk-pc`. */
  marks: Record<string, ReactNode[]>
}
interface PlanTotal {
  key: string
  tone: 1 | 2 | null
  name: string
  note: ReactNode
  /** Underlying → "live/planned" for this account, or null where it is asked for nothing. */
  perUnderlying: Record<string, ReactNode | null>
}

function PlanSheet({
  underlyings,
  accounts,
  lines,
  totals,
  label,
}: {
  underlyings: readonly string[]
  accounts: readonly DeskAccount[]
  lines: readonly PlanLine[]
  totals: readonly PlanTotal[]
  label: string
}) {
  const [wrapRef, overflows] = useOverflowX<HTMLDivElement>()
  const multi = accounts.length > 1
  return (
    <>
      <div className={`dk-gridwrap dk-gridwrap--plan${overflows ? ' dk-gridwrap--scroll' : ''}`} ref={wrapRef}>
        <table className="dk-t dk-grid dk-grid--plan">
          <thead>
            <tr>
              <th className="dk-fix dk-fix--l">Strategy</th>
              {underlyings.map((u) => (
                <th key={u} className="r dk-u" title={u}>
                  {underlyingShort(u)} <span className="dk-t3">{opensAt(u)}</span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {lines.map((row) => (
              <tr key={row.key}>
                <td className="dk-sn dk-fix dk-fix--l" title={row.title}>
                  {shortName(row.label)}
                  {row.sub && <span className="dk-xs dk-t3"> {row.sub}</span>}
                </td>
                {underlyings.map((u) => (
                  <td key={u} className="dk-u">
                    {row.marks[u]}
                  </td>
                ))}
              </tr>
            ))}
            {totals.map((t, i) => (
              <tr key={t.key} className={`dk-tot${i === 0 ? ' dk-tot--first' : ''}`}>
                <td className="dk-fix dk-fix--l">
                  <Swatch tone={t.tone} />
                  <span className="dk-t2 dk-accname" title={t.name}>
                    {t.name}
                  </span>
                  {/* Under the name, not beside it: beside, the count would widen the fixed column past the underlyings' room. */}
                  <span className="dk-xs dk-plan-count">{t.note}</span>
                </td>
                {underlyings.map((u) => (
                  <td key={u} className="r dk-u dk-xs">
                    {t.perUnderlying[u]}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {overflows && <p className="dk-note dk-gridhint">Scroll sideways for the other underlyings; the strategy stays put.</p>}
      <div className="dk-gl dk-gl--plan" aria-label={label}>
        {multi && <ListColumns accounts={accounts} />}
        {lines.map((row) => (
          <section className="dk-gl__row" key={row.key}>
            <div className="dk-gl__head">
              <span className="dk-gl__name" title={row.title}>
                {row.label}
                {row.sub && <span className="dk-xs dk-t3"> {row.sub}</span>}
              </span>
            </div>
            <div className="dk-gl__cells">
              {underlyings
                .filter((u) => row.marks[u].length > 0)
                .map((u) => (
                  <div className="dk-gl__u" key={u}>
                    <span className="dk-gl__ul dk-t3" title={`${u} · opens ${opensAt(u)}`}>
                      {underlyingShort(u)}
                    </span>
                    {row.marks[u]}
                  </div>
                ))}
            </div>
          </section>
        ))}
        {totals.map((t) => (
          <section className="dk-gl__row dk-gl__tot" key={t.key}>
            <div className="dk-gl__head">
              <span className="dk-gl__name dk-t2">
                <Swatch tone={t.tone} />
                {t.name}
              </span>
              <span className="dk-xs">{t.note}</span>
            </div>
            <div className="dk-gl__cells dk-gl__cells--tot dk-xs">
              {underlyings.map((u) =>
                t.perUnderlying[u] == null ? null : (
                  <span key={u} className="dk-n">
                    <span className="dk-t3">{underlyingShort(u)}</span> {t.perUnderlying[u]}
                  </span>
                ),
              )}
            </div>
          </section>
        ))}
      </div>
    </>
  )
}

/** The mark of an account with nothing here, and why in its title. */
function NotAsked({ why }: { why: string }) {
  return (
    <div className="dk-pc">
      <span className="dk-t3 dk-xs" title={why}>
        —
      </span>
    </div>
  )
}

/**
 * The morning plan as the file asks for it (admin: GET /api/Desk/plan), each
 * run live or not by the API's own test (Running, runner alive), so a row
 * left Running by a dead runner reads as not live. A row per strategy line of
 * the file; in each cell, a line per account the line deploys into.
 */
function FilePlan({ view, links }: { view: DeskView; links: DeskLinks }) {
  const { plan, file, ready, missing, searched, error } = useDeskPlan(view)
  const head = (
    <PanelHead
      title="Morning plan"
      meta={plan ? `${planFileName(file)} · ${plan.live} of ${plan.planned} live` : planFileName(file)}
      more={links.runs ? { to: links.runs, label: 'Runs' } : null}
    />
  )
  if (!ready) return <>{head}<Waiting>Reading the plan…</Waiting></>
  if (missing) {
    return (
      <>
        {head}
        <p className="dk-fail" role="status">
          There is no plan file on the server, so the morning job starts nothing.
        </p>
        {searched.length > 0 && <p className="dk-note">Looked in {searched.join(', ')}.</p>}
      </>
    )
  }
  if (!plan) return <>{head}<Failed what="The plan" error={error} /></>
  if (plan.rows.length === 0) return <>{head}<Waiting>The plan asks for no runs{view.scopeName ? ` in ${view.scopeName}` : ''}.</Waiting></>
  const multi = plan.accounts.length > 1
  const toneOf = new Map(view.allAccounts.map((a) => [a.id, a.tone]))
  const toneFor = (userId: number | null) => (userId != null ? (toneOf.get(userId) ?? null) : null)
  // The plan's accounts in the desk's colours; one without a user id has none.
  const accounts: DeskAccount[] = plan.accounts.map((a, i) => ({ id: a.userId ?? -(i + 1), name: a.name, tone: toneFor(a.userId) }))
  const inPlan = new Set(plan.rows.flatMap((r) => Object.values(r.cells).flat()).map((c) => c?.runId))
  const outside = (view.runs ?? []).filter((r) => r.isActive && !inPlan.has(r.runId)).length
  const lines: PlanLine[] = plan.rows.map((row) => ({
    key: `${row.line ?? ''}:${row.strategy}`,
    label: row.label,
    sub: row.lots != null ? `${row.lots}L` : null,
    title: [row.label, row.line != null ? `line ${row.line}` : '', row.target ?? '', row.onlyAccounts.length ? `only ${row.onlyAccounts.join(', ')}` : '']
      .filter(Boolean)
      .join(' · '),
    marks: Object.fromEntries(
      plan.underlyings.map((u) => [
        u,
        row.cells[u].every((c) => c == null)
          ? []
          : row.cells[u].map((c, i) =>
              c ? (
                <div className="dk-pc" key={plan.accounts[i].name}>
                  {multi && <Swatch tone={toneFor(c.userId)} cell />}
                  {c.live && c.runId != null ? (
                    <Link to={`${links.runBase}/${c.runId}`} className="pos" title={`${c.account}: live as run #${c.runId}`}>
                      ✓
                    </Link>
                  ) : (
                    <Chip tone="warn" title={`${c.account}: asked for, and not running with a live runner`}>
                      <span className="dk-long">not live</span>
                      <span className="dk-short">off</span>
                    </Chip>
                  )}
                </div>
              ) : (
                <NotAsked key={plan.accounts[i].name} why={`Not asked of ${plan.accounts[i].name}`} />
              ),
            ),
      ]),
    ),
  }))
  // Each account's count under each underlying: how many of its runs there are live.
  const totals: PlanTotal[] = plan.accounts.map((a, i) => ({
    key: a.name,
    tone: toneFor(a.userId),
    name: a.name,
    note: (
      <>
        <span className={a.live < a.planned ? 'warn' : 'dk-t2'}>
          {a.live} of {a.planned} live
        </span>
        {a.userId == null && <span className="warn"> · no such account</span>}
      </>
    ),
    perUnderlying: Object.fromEntries(
      plan.underlyings.map((u) => {
        const asked = plan.rows.map((r) => r.cells[u][i]).filter((c): c is PlanCell => c != null)
        if (asked.length === 0) return [u, null]
        const live = asked.filter((c) => c.live).length
        return [
          u,
          <span key={u} className={live < asked.length ? 'warn' : 'dk-t2'}>
            {live}/{asked.length}
          </span>,
        ]
      }),
    ),
  }))
  return (
    <>
      {head}
      <PlanSheet underlyings={plan.underlyings} accounts={accounts} lines={lines} totals={totals} label="The morning plan by strategy" />
      {plan.warnings.length > 0 && (
        <ul className="dk-list dk-warnings" aria-label="What the morning job would read differently">
          {plan.warnings.map((w) => (
            <li key={w}>{w}</li>
          ))}
        </ul>
      )}
      {outside > 0 && (
        <div className="dk-foot">
          {outside} live run{outside === 1 ? ' is' : 's are'} not in the plan: started by hand.
        </div>
      )}
    </>
  )
}

/** Before the open: the operator reads the plan file against what is live; a trader, their own deployed runs. */
export function PlanGrid({ view, links }: { view: DeskView; links: DeskLinks }) {
  return view.isAdmin ? <FilePlan view={view} links={links} /> : <DeployedGrid view={view} links={links} />
}

/** A trader's morning: their own runs, deployed and waiting for the session (the plan file is the operator's). */
function DeployedGrid({ view, links }: { view: DeskView; links: DeskLinks }) {
  const head = <PanelHead title="Deployed" meta="✓ waiting for its session" more={links.runs ? { to: links.runs, label: 'My runs' } : null} />
  if (view.runsError && !view.grid) return <>{head}<Failed what="The runs" error={view.runsError} /></>
  if (!view.grid) return <>{head}<Waiting>Reading today’s runs…</Waiting></>
  const { grid } = view
  if (grid.rows.length === 0) return <>{head}<Waiting>Nothing deployed yet today.</Waiting></>
  const multi = grid.accounts.length > 1
  const lines: PlanLine[] = grid.rows.map((row) => ({
    key: row.strategy,
    label: row.label,
    sub: null,
    title: row.label,
    marks: Object.fromEntries(
      grid.underlyings.map((u) => [
        u,
        row.cells[u].every((c) => c == null)
          ? []
          : row.cells[u].map((c, i) =>
              c ? (
                <div className="dk-pc" key={grid.accounts[i].id}>
                  {multi && <Swatch tone={grid.accounts[i].tone} cell />}
                  {c.live ? (
                    <span className="pos" title={`${grid.accounts[i].name}: deployed ${istHm(c.runs[0].startedUtc)}${c.waiting ? ', waiting for its session' : ', trading'}`}>
                      ✓
                    </span>
                  ) : (
                    <Chip tone="warn" title={c.runs[c.runs.length - 1].stopReason ?? 'Stopped'}>
                      off
                    </Chip>
                  )}
                </div>
              ) : (
                <NotAsked key={grid.accounts[i].id} why={`${grid.accounts[i].name} has not deployed this here`} />
              ),
            ),
      ]),
    ),
  }))
  return (
    <>
      {head}
      <PlanSheet underlyings={grid.underlyings} accounts={grid.accounts} lines={lines} totals={[]} label="Deployed runs by strategy" />
    </>
  )
}
