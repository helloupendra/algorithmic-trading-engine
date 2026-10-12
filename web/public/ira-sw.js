/*
 * The Ira app's service worker (scope /ira): its shell, so the app launches
 * from the home screen with no network and says so, instead of showing the
 * browser's offline page.
 *
 * What it keeps: the /ira page itself, its bundles (/assets/, named by their
 * hash, so a kept one never goes stale), its icons and manifest, the fonts.
 * What it never touches: /api and /hubs. A question, an answer, a token, a
 * PIN or a recording is never cached; those requests go to the network as if
 * there were no worker at all.
 *
 * The page comes from the network first, so a deploy's new bundles are picked
 * up at once; the copy kept is only for when the network is not there.
 */

const CACHE = 'ira-shell-v1'
const SHELL = ['/ira', '/ira.webmanifest', '/brand/ira-mark.svg', '/brand/ira-192.png']
/** Bundles kept at most: each deploy adds new names, and the oldest are let go. */
const MAX_ENTRIES = 80

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(CACHE)
      .then((cache) => cache.addAll(SHELL))
      .catch(() => undefined)
      .then(() => self.skipWaiting()),
  )
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('ira-shell-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  )
})

function neverCached(url) {
  return url.pathname === '/api' || url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/')
}

function isShellFile(url) {
  return (
    url.pathname.startsWith('/assets/') ||
    url.pathname.startsWith('/fonts/') ||
    url.pathname.startsWith('/brand/ira-') ||
    url.pathname === '/ira.webmanifest'
  )
}

async function keep(request, response) {
  if (!response || !response.ok || response.type !== 'basic') return
  const cache = await caches.open(CACHE)
  await cache.put(request, response)
  const keys = await cache.keys()
  for (const old of keys.slice(0, Math.max(0, keys.length - MAX_ENTRIES))) {
    if (!SHELL.includes(new URL(old.url).pathname)) await cache.delete(old)
  }
}

self.addEventListener('fetch', (event) => {
  const request = event.request
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin !== self.location.origin || neverCached(url)) return

  if (request.mode === 'navigate') {
    // The page: the network's, kept as the shell; the kept shell when there is no network.
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) event.waitUntil(keep(new Request('/ira'), response.clone()))
          return response
        })
        .catch(() => caches.match('/ira').then((kept) => kept || Response.error())),
    )
    return
  }

  if (isShellFile(url)) {
    // A bundle is named by its hash: the kept copy is the right one. Others are asked again in the background.
    event.respondWith(
      caches.match(request).then((kept) => {
        const fetchAndKeep = () =>
          fetch(request).then((response) => {
            event.waitUntil(keep(request, response.clone()))
            return response
          })
        if (kept) {
          if (!url.pathname.startsWith('/assets/')) event.waitUntil(fetchAndKeep().catch(() => undefined))
          return kept
        }
        return fetchAndKeep()
      }),
    )
  }
})
