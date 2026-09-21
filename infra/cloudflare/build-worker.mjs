/**
 * Bakes down.html into worker.js, so the Worker is one file with nothing to
 * fetch — the page has to render on the day the origin cannot be reached at
 * all, which is exactly when an external asset would fail too.
 *
 *   node infra/cloudflare/build-worker.mjs
 *
 * Run it after editing down.html, and paste the result into the Cloudflare
 * dashboard (see README.md).
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const page = readFileSync(join(here, 'down.html'), 'utf8')

// The page goes into a template literal, so these three characters are all
// that can break out of it.
const escaped = page.replace(/\\/g, '\\\\').replace(/`/g, '\\`').replace(/\$\{/g, '\\${')

const worker = `// GENERATED FILE — edit down.html and run: node infra/cloudflare/build-worker.mjs
//
// Cloudflare Worker for openfno.com. It does nothing at all while the origin
// answers; when the origin cannot be reached it serves the offline page
// instead of Cloudflare's own "Bad gateway", which is the only thing a visitor
// saw when the API was restarting.
//
// What it deliberately does NOT do: touch /api or /hubs. The API answers 502
// itself when a call to a broker fails, and a Worker that turned those into a
// pretty page would hide the broker's own words from the console. Those paths
// pass straight through, and only a connection that failed outright is
// reported — as JSON, because the console parses JSON.

const DOWN_PAGE = \`${escaped}\`

/** Statuses Cloudflare returns when it could not reach the tunnel at all. */
const ORIGIN_DOWN = new Set([502, 504, 521, 522, 523, 524, 525, 526, 530])

export default {
  async fetch(request) {
    const url = new URL(request.url)
    const isApi = url.pathname.startsWith('/api') || url.pathname.startsWith('/hubs')

    // A WebSocket cannot be answered with a page, and the console's live feed
    // is one; hand it over untouched.
    if ((request.headers.get('Upgrade') || '').toLowerCase() === 'websocket') {
      return fetch(request)
    }

    let response
    try {
      response = await fetch(request)
    } catch (error) {
      return offline(isApi, String(error && error.message ? error.message : error))
    }

    if (!isApi && ORIGIN_DOWN.has(response.status)) return offline(false, 'HTTP ' + response.status)
    return response
  },
}

function offline(isApi, reason) {
  const headers = { 'cache-control': 'no-store', 'retry-after': '30' }

  if (isApi) {
    return new Response(
      JSON.stringify({ code: 'ORIGIN_DOWN', message: 'The server is not answering (' + reason + '). It is usually restarting; try again in a moment.' }),
      { status: 503, headers: { ...headers, 'content-type': 'application/json; charset=utf-8' } },
    )
  }

  return new Response(DOWN_PAGE, {
    status: 503,
    headers: { ...headers, 'content-type': 'text/html; charset=utf-8' },
  })
}
`

writeFileSync(join(here, 'worker.js'), worker)
console.log(`worker.js written (${(worker.length / 1024).toFixed(1)} KB)`)
