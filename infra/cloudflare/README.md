# The offline page

When the API is restarting — a deploy, a health-check restart, a reboot — the
Cloudflare tunnel has nothing to talk to, and a visitor to openfno.com gets
Cloudflare's own grey **Bad gateway** (or *error 1033* when the machine itself
is off). This replaces that with a page that says what is happening, says what
is safe, and retries on its own.

| File | What it is |
| --- | --- |
| `down.html` | The page. Self-contained: no fonts, no scripts, no images from anywhere |
| `worker.js` | **Generated.** A Cloudflare Worker with that page baked into it |
| `build-worker.mjs` | Bakes one into the other: `node infra/cloudflare/build-worker.mjs` |

## Why a Worker

The tunnel maps one hostname to one service; there is no second service to fail
over to, and a fallback that lives on the same machine is down whenever the
machine is. The Worker runs at Cloudflare's edge, so it answers even when the
server is off entirely. It is on the free plan.

## Putting it live (once)

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Start with Hello
   World** → **Deploy** → **Edit code**.
2. Replace everything with the contents of `worker.js` → **Deploy**.
3. That Worker → **Settings** → **Domains & Routes** → **Add route**:
   `openfno.com/*`, zone `openfno.com`. Add a second for `www.openfno.com/*`.
4. Check it: stop the API (`ssh AlgoTradingEngine 'pkill -f AlgoTrading.Api'`),
   open the site — the offline page should appear, and the console should come
   back by itself within about ninety seconds.

To change the page afterwards: edit `down.html`, run the build, paste `worker.js`
again.

## What it does not touch

`/api/*` and `/hubs/*` pass through untouched, including WebSockets. The API
answers `502` itself when a call to a broker fails, and a Worker that turned
those into a pretty page would hide the broker's own words from the console. A
connection that fails outright on those paths is reported as JSON, which is what
the console parses.

## While you are in the dashboard

Cloudflare injects its analytics beacon (`static.cloudflareinsights.com`) into
every HTML page. The console's Content-Security-Policy allows scripts from this
origin only, so the browser refuses it and logs a CSP violation on every page
load. Turn it off — **Analytics & Logs → Web Analytics →** the site **→ Manage
→ Disable automatic setup** — rather than widening the policy: the header is
what makes an injected script unable to run.
