/*
 * The console's theme before the first paint: the same rule as
 * src/lib/theme.ts (openfno.theme = light | dark | system, default light).
 * A file of its own, loaded by a blocking <script src> in index.html's head,
 * because the API's CSP (script-src 'self') refuses inline scripts: inline,
 * it never ran in production and a dark console flashed light on every load.
 * <html> already says light, so a page where this cannot run still paints the
 * default.
 */
(function () {
  try {
    var pref = localStorage.getItem('openfno.theme')
    var dark = pref === 'dark' || (pref === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    var theme = dark ? 'dark' : 'light'
    document.documentElement.setAttribute('data-theme', theme)
    document.documentElement.style.colorScheme = theme
    var meta = document.querySelector('meta[name="theme-color"]')
    if (meta) meta.setAttribute('content', dark ? '#0e1115' : '#f6f8f9')
  } catch {
    // Storage blocked (a private window, blocked site data): the default stands.
  }
})()
