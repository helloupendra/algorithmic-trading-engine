// Light / Dark / System in the footer: the console's own key (openfno.theme),
// applied the way public/theme-boot.js applied it before the first paint.
const KEY = 'openfno.theme'
const mq = matchMedia('(prefers-color-scheme: dark)')
const read = () => {
  try {
    return localStorage.getItem(KEY) || 'light'
  } catch {
    return 'light'
  }
}
function apply(pref) {
  const dark = pref === 'dark' || (pref === 'system' && mq.matches)
  const theme = dark ? 'dark' : 'light'
  document.documentElement.setAttribute('data-theme', theme)
  document.documentElement.style.colorScheme = theme
  const meta = document.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', dark ? '#0e1115' : '#f6f8f9')
  for (const b of document.querySelectorAll('[data-theme-pick]')) b.setAttribute('aria-pressed', String(b.dataset.themePick === pref))
}
apply(read())
for (const b of document.querySelectorAll('[data-theme-pick]')) {
  b.addEventListener('click', () => {
    try {
      localStorage.setItem(KEY, b.dataset.themePick)
    } catch {
      // Storage blocked: the choice still applies to this page.
    }
    apply(b.dataset.themePick)
  })
}
mq.addEventListener('change', () => {
  if (read() === 'system') apply('system')
})
