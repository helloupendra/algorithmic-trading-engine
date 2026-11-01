/**
 * Data → Mutual funds → compare (/data/mutual-funds/compare?codes=120716,100822):
 * two to four schemes side by side, every figure ending on the latest NAV day
 * all of them have. The rows come from lib/mutualFunds.ts (compareSections),
 * so each figure reads as the scheme's own report reads it, and every "—"
 * carries its reason in text. The address is the comparison: it can be shared.
 *
 * Facts only: the columns keep the order of the address, and nothing marks a
 * column as ahead of another.
 */

import { Fragment } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useMutualFundCompare } from '../../lib/queries'
import {
  MF_COMPARE_MAX,
  MF_COMPARE_MIN,
  compareAsOfLine,
  compareSections,
  optionLabel,
  parseCompareCodes,
  planLabel,
  reportHref,
  splitCategory,
  type MutualFundCompare,
} from '../../lib/mutualFunds'
import { Panel, QueryBoundary } from '../../components/ui'
import './data.css'

function CompareTable({ data }: { data: MutualFundCompare }) {
  const sections = compareSections(data.funds)
  return (
    <>
      <p className="mf-asof" role="status">
        {compareAsOfLine(data.asOf, data.funds.length)}
      </p>

      {data.notes.length > 0 && (
        <div className="alert alert--warn alert--stack" role="status">
          <b>Read with these</b>
          <ul className="mf-list mf-flush">
            {data.notes.map((n, i) => (
              <li key={i}>{n}</li>
            ))}
          </ul>
        </div>
      )}

      <Panel title="Side by side">
        <div className="tablewrap">
          <table className="table table--sticky mf-compare">
            <thead>
              <tr>
                <th scope="col">Scheme</th>
                {data.funds.map((f) => (
                  <th key={f.scheme.schemeCode} scope="col">
                    <Link to={reportHref(f.scheme.schemeCode)}>{f.scheme.schemeName}</Link>
                    <span className="cell-sub">
                      {f.scheme.amc || '—'} · {planLabel(f.scheme.plan)} · {f.scheme.optionLabel || optionLabel(f.scheme.option)}
                    </span>
                    <span className="cell-sub">{f.categoryName || splitCategory(f.scheme.category).name}</span>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {sections.map((section) => (
                <Fragment key={section.title}>
                  <tr className="mf-compare__section">
                    <th scope="colgroup">{section.title}</th>
                    <td colSpan={data.funds.length} />
                  </tr>
                  {section.rows.map((row) => (
                    <tr key={row.key}>
                      <th scope="row" title={row.hint}>
                        {row.label}
                      </th>
                      {row.cells.map((cell, i) => (
                        <td key={data.funds[i]?.scheme.schemeCode ?? i}>
                          <span className={cell.tone ?? (cell.missing ? 'muted' : '')}>{cell.text}</span>
                          {cell.sub && <span className="cell-sub">{cell.sub}</span>}
                          {cell.missing && <span className="cell-sub">{cell.missing}</span>}
                        </td>
                      ))}
                    </tr>
                  ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
        <p className="small-note">
          CAGR is the compound annual growth of the NAV. Category position is the place by return among schemes of the
          same category, plan and option with a return for the period, 1st being the highest. Against the benchmark is
          the fund&rsquo;s CAGR minus the benchmark&rsquo;s, in percentage points (pp); for an index fund it is the
          tracking difference. SIP and lump-sum values are on the last day, with no tax, exit load or stamp duty taken off.
          Each scheme&rsquo;s report has the dates and sources of every figure.
        </p>
      </Panel>

      {data.disclaimer && (
        <p className="mf-disclaimer" role="note">
          {data.disclaimer}
        </p>
      )}
    </>
  )
}

export function MutualFundComparePage() {
  const [params] = useSearchParams()
  const { codes, dropped } = parseCompareCodes(params.get('codes'))
  const compare = useMutualFundCompare(codes)

  return (
    <div className="page data-page">
      <header className="page__header">
        <div>
          <p className="mf-crumb">
            <Link to="/data/mutual-funds">Mutual funds</Link> / compare
          </p>
          <h1 className="page__title">Compare funds</h1>
          <p className="page__subtitle">
            {MF_COMPARE_MIN} to {MF_COMPARE_MAX} schemes side by side, every figure ending on the same NAV day. The address
            is the comparison: share it to share this view.
          </p>
        </div>
      </header>

      {dropped > 0 && (
        <p className="small-note small-note--warn" role="status">
          The address names {codes.length + dropped} schemes; the first {MF_COMPARE_MAX} are compared.
        </p>
      )}

      {codes.length < MF_COMPARE_MIN ? (
        <Panel>
          <p className="empty">
            {codes.length === 0 ? 'No schemes picked yet.' : `Scheme ${codes[0]} is picked; one more is needed.`} Tick{' '}
            {MF_COMPARE_MIN} to {MF_COMPARE_MAX} schemes in the search on <Link to="/data/mutual-funds">Mutual funds</Link>{' '}
            and press Compare.
          </p>
        </Panel>
      ) : (
        <QueryBoundary query={compare}>{(data) => <CompareTable data={data} />}</QueryBoundary>
      )}
    </div>
  )
}
