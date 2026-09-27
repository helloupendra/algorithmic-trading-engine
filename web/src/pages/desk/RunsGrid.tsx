/**
 * Runs · net P&L: the object read all day. Strategy down the side,
 * underlying across, and in each cell one line per account (admin above,
 * coderforchange below, in the account colours), net of the charges of its
 * fills. Marks say what the number alone cannot: stopped by a risk rule,
 * restarted, a leg carried, or stopped and not restarted. Totals per account
 * at the bottom, per strategy on the right, with the trades and the charges
 * that turn a gross into a net.
 *
 * Before the open the same grid is the morning plan: a tick per deployed run,
 * waiting for its session.
 */

import { Fragment } from 'react'
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

function StrategyName({ row, view }: { row: GridRow; view: DeskView }) {
  const only = view.accounts.length > 1 && row.accountIds.length === 1 ? view.accounts.find((a) => a.id === row.accountIds[0])?.name : null
  const detail = [`${row.runs} run${row.runs === 1 ? '' : 's'}`, row.lots != null ? `${row.lots} lot${row.lots === 1 ? '' : 's'}` : 'mixed lots', only ? `${only} only` : '']
    .filter(Boolean)
    .join(' · ')
  return (
    <>
      <span className="dk-long">{row.label}</span>
      <span className="dk-short">{shortName(row.label)}</span>
      <div className="dk-xs dk-t3 dk-sub">{detail}</div>
    </>
  )
}

export function RunsGrid({ view, links }: { view: DeskView; links: DeskLinks }) {
  const meta = view.phase === 'post' ? 'final · net after charges' : 'net after charges · live'
  const head = <PanelHead title="Runs · net P&L" meta={meta} more={links.runs ? { to: links.runs, label: view.isAdmin ? 'Live runner' : 'My runs' } : null} />
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
      <div className="dk-gridwrap">
        <table className="dk-t dk-grid">
          <thead>
            <tr>
              <th>Strategy</th>
              {grid.underlyings.map((u) => (
                <th key={u} className="r dk-u">
                  <span className="dk-long">{u}</span>
                  <span className="dk-short">{underlyingShort(u)}</span>
                </th>
              ))}
              <th className="r dk-hide-s">Trades</th>
              <th className="r dk-hide-s">Charges</th>
              <th className="r">Net</th>
            </tr>
          </thead>
          <tbody>
            {grid.rows.map((row) => (
              <tr key={row.strategy}>
                <td className="dk-sn">
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
                <td className="r dk-n">
                  <b className={toneClass(row.figures.net)} style={{ fontWeight: 500 }} title={formatInrSigned(row.figures.net)}>
                    {compactInr(row.figures.net)}
                  </b>
                </td>
              </tr>
            ))}
            {grid.totals.map((t, i) => (
              <tr key={t.account.id} className={`dk-tot${i === 0 ? ' dk-tot--first' : ''}`}>
                <td>
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
                <td className={`r dk-n dk-net ${toneClass(t.figures.net)}`} title={formatInrSigned(t.figures.net)}>
                  {compactInr(t.figures.net)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
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

/** The account line under the plan: deployed of planned, where the plan says. */
function PlanFoot({ view }: { view: DeskView }) {
  const { plan } = useDeskPlan(view)
  const first = view.runs?.map((r) => r.startedUtc).filter(Boolean).sort()[0] ?? null
  return (
    <>
      {view.grid!.totals.map((t) => {
        const planned = plan?.perAccount.find((a) => a.name === t.account.name)?.runs
        return (
          <tr key={t.account.id} className="dk-tot">
            <td>
              <Swatch tone={t.account.tone} />
              <span className="dk-t2">{t.account.name}</span>
            </td>
            <td colSpan={view.grid!.underlyings.length} className="r dk-t2 dk-xs">
              {planned != null ? `${t.runs} of ${planned} runs deployed` : `${t.runs} runs deployed`}
              {first ? ` ${istHm(first)}` : ''}
            </td>
          </tr>
        )
      })}
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

export function PlanGrid({ view, links }: { view: DeskView; links: DeskLinks }) {
  const head = <PanelHead title="Morning plan" meta="✓ deployed, waiting for its session" more={links.runs ? { to: links.runs, label: view.isAdmin ? 'Live runner' : 'My runs' } : null} />
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
            {view.isAdmin ? <PlanFoot view={view} /> : null}
          </tbody>
        </table>
      </div>
    </>
  )
}
