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
 * (a container query in desk.css), not the window's, picks the short names
 * and drops the trades and charges columns; under about 520px the same grid
 * is a list, a block per strategy with a line per underlying, because eight
 * columns of figures do not fit a phone whatever their width.
 *
 * Before the open the panel beside it is the morning plan: for the operator,
 * the plan file read against what is live (GET /api/Desk/plan); for a
 * trader, a tick per run of theirs deployed and waiting for its session.
 */

import { Fragment, useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import type { CellMark, DeskAccount, GridCell, GridRow } from '../../lib/desk'
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

function StrategyName({ row, view }: { row: GridRow; view: DeskView }) {
  return (
    <>
      <span className="dk-long">{row.label}</span>
      <span className="dk-short">{shortName(row.label)}</span>
      <div className="dk-xs dk-t3 dk-sub">{rowDetail(row, view)}</div>
    </>
  )
}

/**
 * Whether a scroll container's content is wider than it is, kept current as
 * either resizes: the table grows a column per underlying that ran, and the
 * panel shrinks with the window.
 */
function useOverflowX<T extends HTMLElement>(): [(el: T | null) => void, boolean] {
  const [el, setEl] = useState<T | null>(null)
  const [over, setOver] = useState(false)
  useEffect(() => {
    if (!el) return
    const check = () => setOver(el.scrollWidth > el.clientWidth + 1)
    const observer = new ResizeObserver(check)
    observer.observe(el)
    if (el.firstElementChild) observer.observe(el.firstElementChild)
    check()
    return () => observer.disconnect()
  }, [el])
  return [useCallback((next: T | null) => setEl(next), []), over]
}

/**
 * The grid as a phone reads it: a block per strategy with its net on the
 * right, then a line per underlying it ran with an account's figure per
 * column, and the accounts' totals in the same shape. Rendered beside the
 * table; desk.css shows one or the other by the panel's width.
 */
function GridList({ view, links, multi }: { view: DeskView; links: DeskLinks; multi: boolean }) {
  const grid = view.grid!
  return (
    <div className="dk-gl" aria-label="Runs by strategy">
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
  const [wrapRef, overflows] = useOverflowX<HTMLDivElement>()
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
              <th className="r dk-hide-s">Trades</th>
              <th className="r dk-hide-s">Charges</th>
              <th className="r dk-fix dk-fix--r">Net</th>
            </tr>
          </thead>
          <tbody>
            {grid.rows.map((row) => (
              <tr key={row.strategy}>
                <td className="dk-sn dk-fix dk-fix--l">
                  <StrategyName row={row} view={view} />
                </td>
                {grid.underlyings.map((u) => (
                  <td key={u} className="dk-u">
                    {row.cells[u].every((c) => c == null) ? null : row.cells[u].map((c, i) => <CellLine key={grid.accounts[i].id} cell={c} account={grid.accounts[i]} multi={multi} links={links} />)}
                  </td>
                ))}
                <td className="r dk-n dk-t2 dk-hide-s">{row.figures.trades.toLocaleString('en-IN')}</td>
                <td className="r dk-n dk-t3 dk-hide-s" title={formatInrWhole(row.figures.charges)}>
                  {compactInr(row.figures.charges, false)}
                </td>
                <td className="r dk-n dk-fix dk-fix--r">
                  <b className={toneClass(row.figures.net)} style={{ fontWeight: 500 }} title={formatInrSigned(row.figures.net)}>
                    {compactInr(row.figures.net)}
                  </b>
                </td>
              </tr>
            ))}
            {grid.totals.map((t, i) => (
              <tr key={t.account.id} className={`dk-tot${i === 0 ? ' dk-tot--first' : ''}`}>
                <td className="dk-fix dk-fix--l">
                  {multi && <Swatch tone={t.account.tone} />}
                  <span className="dk-t2 dk-accname" title={t.account.name}>
                    {multi ? t.account.name : 'Total'}
                  </span>{' '}
                  <span className="dk-xs dk-t3 dk-sub">{t.runs} runs</span>
                </td>
                {grid.underlyings.map((u) => {
                  const f = t.byUnderlying[u]
                  return (
                    <td key={u} className={`r dk-n dk-u ${f ? toneClass(f.net) : ''}`} title={f ? formatInrSigned(f.net) : undefined}>
                      {f ? compactInr(f.net) : ''}
                    </td>
                  )
                })}
                <td className="r dk-n dk-t2 dk-hide-s">{t.figures.trades.toLocaleString('en-IN')}</td>
                <td className="r dk-n dk-t3 dk-hide-s" title={formatInrWhole(t.figures.charges)}>
                  {compactInr(t.figures.charges, false)}
                </td>
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
                  {a.name} {i === 0 ? 'above' : i === grid.accounts.length - 1 ? 'below' : 'next'}
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
  const inPlan = new Set(plan.rows.flatMap((r) => Object.values(r.cells).flat()).map((c) => c?.runId))
  const outside = (view.runs ?? []).filter((r) => r.isActive && !inPlan.has(r.runId)).length
  return (
    <>
      {head}
      <div className="dk-gridwrap">
        <table className="dk-t dk-grid">
          <thead>
            <tr>
              <th>Strategy</th>
              {plan.underlyings.map((u) => (
                <th key={u} className="r">
                  {underlyingShort(u)} <span className="dk-t3">{opensAt(u)}</span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {plan.rows.map((row) => (
              <tr key={row.line ?? row.strategy}>
                <td
                  className="dk-sn"
                  title={[row.label, row.line != null ? `line ${row.line}` : '', row.target ?? '', row.onlyAccounts.length ? `only ${row.onlyAccounts.join(', ')}` : '']
                    .filter(Boolean)
                    .join(' · ')}
                >
                  {shortName(row.label)}
                  {row.lots != null && <span className="dk-xs dk-t3"> {row.lots}L</span>}
                </td>
                {plan.underlyings.map((u) => (
                  <td key={u}>
                    {row.cells[u].every((c) => c == null)
                      ? null
                      : row.cells[u].map((c, i) => (
                          <div className="dk-pc" key={plan.accounts[i].name}>
                            {c ? (
                              <>
                                {multi && <Swatch tone={toneFor(c.userId)} cell />}
                                {c.live && c.runId != null ? (
                                  <Link to={`${links.runBase}/${c.runId}`} className="pos" title={`${c.account}: live as run #${c.runId}`}>
                                    ✓
                                  </Link>
                                ) : (
                                  <Chip tone="warn" title={`${c.account}: asked for, and not running with a live runner`}>
                                    not live
                                  </Chip>
                                )}
                              </>
                            ) : (
                              <span className="dk-t3 dk-xs" title={`Not asked of ${plan.accounts[i].name}`}>
                                —
                              </span>
                            )}
                          </div>
                        ))}
                  </td>
                ))}
              </tr>
            ))}
            {plan.accounts.map((a, i) => (
              <tr key={a.name} className={`dk-tot${i === 0 ? ' dk-tot--first' : ''}`}>
                <td>
                  <Swatch tone={toneFor(a.userId)} />
                  <span className="dk-t2">{a.name}</span>
                  {a.userId == null && <span className="dk-xs warn"> no such account</span>}
                </td>
                <td colSpan={plan.underlyings.length} className={`r dk-xs ${a.live < a.planned ? 'warn' : 'dk-t2'}`}>
                  {a.live} of {a.planned} live
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
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
  return (
    <>
      {head}
      <div className="dk-gridwrap">
        <table className="dk-t dk-grid">
          <thead>
            <tr>
              <th>Strategy</th>
              {grid.underlyings.map((u) => (
                <th key={u} className="r">
                  {underlyingShort(u)} <span className="dk-t3">{opensAt(u)}</span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {grid.rows.map((row) => (
              <tr key={row.strategy}>
                <td className="dk-sn" title={row.label}>
                  {shortName(row.label)}
                </td>
                {grid.underlyings.map((u) => (
                  <td key={u}>
                    {row.cells[u].every((c) => c == null)
                      ? null
                      : row.cells[u].map((c, i) => (
                          <div className="dk-pc" key={grid.accounts[i].id}>
                            {c ? (
                              <>
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
                              </>
                            ) : (
                              <span className="dk-t3 dk-xs">—</span>
                            )}
                          </div>
                        ))}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}
