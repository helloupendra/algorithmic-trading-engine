/**
 * The Ira app's shell: the built index.html with the phone app's own head, as
 * dist/ira.html (written by vite.config.ts after the bundle; the API serves it
 * for /ira, ConsoleSite.cs). The same bundle, the same React app: only what a
 * phone reads before any script runs changes, so Android finds the Ira
 * manifest and an iPhone's Add to Home Screen takes Ira's icon and name.
 *
 * Every swap must find its line: when index.html changes under it, the build
 * fails here rather than shipping a shell that installs the console instead.
 */

export interface ShellSwap {
  /** What it changes, for the error when the line is missing. */
  what: string
  find: RegExp
  replace: string
}

export const IRA_SHELL_SWAPS: readonly ShellSwap[] = [
  { what: 'the manifest', find: /<link rel="manifest" href="[^"]*"\s*\/?>/, replace: '<link rel="manifest" href="/ira.webmanifest" />' },
  { what: 'the home-screen icon', find: /<link rel="apple-touch-icon" href="[^"]*"\s*\/?>/, replace: '<link rel="apple-touch-icon" href="/brand/ira-apple-touch-icon.png" />' },
  { what: 'the tab icon', find: /<link rel="icon" type="image\/svg\+xml" href="[^"]*"\s*\/?>/, replace: '<link rel="icon" type="image/svg+xml" href="/brand/ira-mark.svg" />' },
  // Apple's own line, and the standard one Chrome now asks for beside it.
  {
    what: 'the standalone line',
    find: /<meta name="apple-mobile-web-app-capable" content="yes"\s*\/?>/,
    replace: '<meta name="apple-mobile-web-app-capable" content="yes" />\n    <meta name="mobile-web-app-capable" content="yes" />',
  },
  { what: 'the home-screen name', find: /<meta name="apple-mobile-web-app-title" content="[^"]*"\s*\/?>/, replace: '<meta name="apple-mobile-web-app-title" content="Ira" />' },
  { what: 'the title', find: /<title>[^<]*<\/title>/, replace: '<title>Ira · OpenFNO</title>' },
  // The console's ground (--bg0), not the public pages' black: the status bar and the page are one colour.
  { what: 'the browser colour', find: /<meta name="theme-color" content="[^"]*"\s*\/?>/, replace: '<meta name="theme-color" content="#070b11" />' },
  { what: 'the robots line', find: /<meta name="robots" content="[^"]*"\s*\/?>/, replace: '<meta name="robots" content="noindex,nofollow" />' },
  { what: 'the loading label', find: /Loading the console/, replace: 'Ira' },
]

/** index.html as the Ira app's shell; throws naming the first line it could not find. */
export function iraShell(indexHtml: string, swaps: readonly ShellSwap[] = IRA_SHELL_SWAPS): string {
  let html = indexHtml
  for (const s of swaps) {
    if (!s.find.test(html)) throw new Error(`ira.html: index.html has no ${s.what} to replace (${s.find})`)
    html = html.replace(s.find, s.replace)
  }
  return html
}
