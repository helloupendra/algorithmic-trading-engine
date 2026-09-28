/**
 * Markets → Option chain: one page for everything read off an underlying's
 * option chain, with a tab per reading. Chain is the strikes now; OI history
 * is how open interest moved at one strike through the session; Levels is
 * the walls, max pain and the straddle's move.
 *
 * These were three pages (the chain, the open-interest page and a section of
 * Market factors), each with its own underlying picker, so looking at
 * BANKNIFTY's walls after its chain meant picking BANKNIFTY twice. The
 * underlying now belongs to the page and travels in the URL with the expiry;
 * each tab is a URL of its own (/markets/chain, /oi, /levels) so a link opens
 * the reading it names. The same page for every role: the API scopes the
 * positions it marks to whoever is asking.
 *
 * The row of underlyings is the recorder's set (lib/optionChain.ts); a link
 * to any other F&O name the instrument master knows opens that chain and
 * joins the row while it is open, instead of silently opening NIFTY.
 */

import { useEffect, useMemo, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'
import { PageTabs } from '../../../components/PageTabs'
import { useFnoUnderlyings } from '../../../lib/queries'
import { CHAIN_VIEWS, UNDERLYINGS, chainUnderlying, chainViewUrl } from '../../../lib/optionChain'
import type { ChainViewKey } from '../../../lib/optionChain'
import { ChainView } from './ChainView'
import { OiHistoryView } from './OiHistoryView'
import { LevelsView } from './LevelsView'
import './chain.css'

export function OptionChainPage({ view }: { view: ChainViewKey }) {
  const [params, setParams] = useSearchParams()
  const known = useFnoUnderlyings()
  const names = useMemo(() => known.data?.map((u) => u.underlying), [known.data])
  const underlying = chainUnderlying(params.get('u'), names)
  const row: readonly string[] = (UNDERLYINGS as readonly string[]).includes(underlying) ? UNDERLYINGS : [...UNDERLYINGS, underlying]
  const undsRef = useRef<HTMLDivElement>(null)

  // On a phone the row scrolls: a link to CRUDEOIL must not open with CRUDEOIL out of sight.
  useEffect(() => {
    const row = undsRef.current
    const on = row?.querySelector<HTMLElement>('[aria-pressed="true"]')
    if (!row || !on) return
    if (on.offsetLeft < row.scrollLeft || on.offsetLeft + on.offsetWidth > row.scrollLeft + row.clientWidth) {
      row.scrollLeft = Math.max(0, on.offsetLeft - 24)
    }
  }, [underlying])

  // Another underlying has other expiries and another session to replay.
  const pick = (name: string) => {
    const next = new URLSearchParams(params)
    next.set('u', name)
    next.delete('expiry')
    next.delete('at')
    setParams(next, { replace: true })
  }

  return (
    <div className="page oc-page ocp">
      <div className="ocp-head">
        <PageTabs
          label="Option chain views"
          className="ocp-views"
          current={view}
          tabs={CHAIN_VIEWS.map((v) => ({ key: v.key, label: v.label, to: chainViewUrl(v.key, params) }))}
        />
        <div className="oc-seg ocp-unds scroll-x" role="group" aria-label="Underlying" ref={undsRef}>
          {row.map((name) => (
            <button key={name} type="button" aria-pressed={underlying === name} onClick={() => pick(name)}>
              {name}
            </button>
          ))}
        </div>
      </div>

      {view === 'chain' && <ChainView underlying={underlying} />}
      {/* Keyed by the underlying: a strike picked on one chain means nothing on another. */}
      {view === 'oi' && <OiHistoryView key={underlying} underlying={underlying} />}
      {view === 'levels' && <LevelsView underlying={underlying} />}
    </div>
  )
}
