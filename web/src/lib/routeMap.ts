/**
 * Every route App.tsx serves today, and where it lives once the URLs move to
 * the workspaces (/desk, /markets, /trade, /research, /system).
 *
 * Only the two home pages redirect so far (to /desk); the rest of this is
 * the table the URL move will be generated from, kept now so that no
 * bookmark, Telegram link or checkup link
 * (CheckupItem.link) is forgotten when it happens. The redirect keeps the
 * query string and fills `:params` by name. routeMap.test.ts fails if App.tsx
 * gains a route this table does not place, or if an entry outlives its route.
 */

export const ROUTE_MOVES: Readonly<Record<string, string>> = {
  // The two home pages, retired: both consoles open on the Desk.
  '/admin': '/desk',
  '/trader': '/desk',

  // Markets.
  '/trader/watchlist': '/markets',
  '/admin/data/commodity': '/markets?group=mcx',
  '/trader/charts': '/markets/chart',
  '/trader/structure': '/markets/chart?layer=structure',
  '/admin/data/structure': '/markets/chart?layer=structure',
  '/admin/data/chain': '/markets/chain',
  '/trader/option-chain': '/markets/chain',
  '/admin/data/open-interest': '/markets/chain/oi',
  '/admin/data/movers': '/markets/movers',
  '/trader/market-movers': '/markets/movers',
  '/trader/movers': '/markets/movers',
  '/admin/data/factors': '/markets/factors',
  '/admin/data/news': '/markets/news',
  '/trader/news': '/markets/news',
  '/admin/data/patterns': '/markets/patterns',
  '/admin/system/patterns': '/markets/patterns',

  // Trade.
  '/admin/strategies': '/trade/runs',
  '/admin/strategies/live': '/trade/runs',
  // The API already refuses another user's run, so one URL serves both consoles.
  '/admin/strategies/runs/:runId': '/trade/runs/:runId',
  '/trader/strategies/runs/:runId': '/trade/runs/:runId',
  '/admin/strategies/library': '/trade/library',
  '/admin/strategies/library/:id': '/trade/library/:id',
  '/trader/strategies/:id/how-it-works': '/trade/library/:id',
  // A trader's strategies page is where they deploy from: the library.
  '/trader/deploy': '/trade/library',
  '/trader/strategies': '/trade/library',
  '/admin/strategies/history': '/trade/history',
  '/trader/strategies/history': '/trade/history',
  // The v1 Simulator pages retire once positions and orders across every book exist.
  '/trader/positions': '/trade/positions',
  '/trader/orders': '/trade/orders',
  '/trader/runs/:id': '/trade/history',
  '/admin/trading': '/trade/ticket',
  '/trader/trading': '/trade/ticket',
  '/admin/system/risk': '/trade/risk',
  '/admin/risk': '/trade/risk',

  // Research.
  '/admin/backtesting': '/research/backtests',
  '/admin/backtesting/new': '/research/backtests/new',
  '/admin/backtesting/runs': '/research/backtests/runs',
  '/admin/backtesting/runs/:id': '/research/backtests/runs/:id',
  '/admin/analysis': '/research/forecasts',
  '/admin/trading/lab': '/research/lab',
  '/trader/trading/lab': '/research/lab',
  '/admin/notebook': '/research/notebook',
  // A board stays full-window, outside the shell.
  '/admin/notebook/:id': '/notebook/:id',

  // System.
  '/admin/system': '/system',
  '/admin/checkup': '/system/checkups',
  '/admin/incidents': '/system/incidents',
  '/admin/system/logs': '/system/log',
  '/admin/system/alerts': '/system/log?source=alerts',
  '/admin/live-alerts': '/system/log?source=alerts',
  '/admin/system/deployments': '/system/log?source=deploys',
  '/admin/data': '/system/data',
  '/admin/data/live': '/system/data/feeds',
  '/admin/ingestion': '/system/data/feeds',
  '/admin/data/historical': '/system/data/historical',
  '/admin/data/instruments': '/system/data/instruments',
  '/admin/instruments': '/system/data/instruments',
  '/admin/system/calendar': '/system/data/calendar',
  '/admin/broker': '/system/connectors',
  // Stays routable as an alias until the OAuth callback, which redirects here,
  // is changed in the same PR.
  '/admin/broker/:providerKey': '/system/connectors/:providerKey',
  '/admin/users': '/system/people',
  '/admin/users/packages': '/system/people/packages',

  // The avatar menu.
  '/trader/account': '/account',
}

/** Routes that keep their URL: the public pages, the fallbacks, and pages already at their new home. */
export const ROUTES_THAT_STAY: readonly string[] = ['/', '/login', '/invite/:token', '/forbidden', '*', '/desk']

/** New homes outside every workspace tab. */
export const HOMES_OUTSIDE_WORKSPACES: readonly string[] = ['/account', '/notebook/:id']

/**
 * Where an old URL goes: the target with `:params` filled from the old path
 * and the old query string kept (the target's own query wins on a clash).
 * Null for a path the table does not move.
 */
export function movedUrl(pathname: string, search = ''): string | null {
  for (const [from, to] of Object.entries(ROUTE_MOVES)) {
    const params = matchRoute(from, pathname)
    if (!params) continue
    const [toPath, toQuery = ''] = to.split('?')
    const path = toPath.replace(/:(\w+)/g, (_, name: string) => encodeURIComponent(params[name] ?? ''))
    const query = new URLSearchParams(search)
    for (const [k, v] of new URLSearchParams(toQuery)) query.set(k, v)
    const qs = query.toString()
    return qs ? `${path}?${qs}` : path
  }
  return null
}

/** The `:params` of `pattern` in `pathname`, or null when it does not match. */
function matchRoute(pattern: string, pathname: string): Record<string, string> | null {
  const a = pattern.split('/')
  const b = pathname.replace(/\/+$/, '').split('/')
  if (a.length !== b.length) return null
  const params: Record<string, string> = {}
  for (let i = 0; i < a.length; i++) {
    if (a[i].startsWith(':')) {
      if (!b[i]) return null
      params[a[i].slice(1)] = decodeURIComponent(b[i])
    } else if (a[i] !== b[i]) return null
  }
  return params
}
