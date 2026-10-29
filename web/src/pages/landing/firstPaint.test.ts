/**
 * What "/" says before any script runs: index.html as the server sends it.
 * A search engine or a link preview that does not run the bundle, and a reader
 * with JavaScript off, get this and not the engine. Until 29 Oct 2026 it was
 * the loader's 26 words, and a web search for openfno.com found nothing.
 *
 * The text is written into index.html by hand, so these tests hold it to the
 * homepage's own words (landing/content.tsx, the run in scene/evidence.ts):
 * change the page's copy and this fails until index.html says the same. The
 * built dist/index.html carries the same markup; Vite only rewrites the script
 * and stylesheet tags.
 */

import { isValidElement, type ReactNode } from 'react'
import { describe, expect, it } from 'vitest'
import { RUN } from '../../scene/evidence'
import { AUTHOR, COPY, GITHUB_URL, LICENSE_URL, LINKEDIN_URL, SCREENS, rupees } from './content'
// Outside web/, where a glob does not reach.
import licenceFile from '../../../../LICENSE?raw'

const read = (glob: Record<string, unknown>) => Object.values(glob)[0] as string
const indexHtml = read(import.meta.glob('../../../index.html', { query: '?raw', import: 'default', eager: true }))
const publicFiles = Object.keys(import.meta.glob('../../../public/brand/*', { query: '?url', import: 'default' })).map((k) => k.replace('../../../public', ''))

/** The licences this page knows how to name, by the SPDX id the LICENSE file carries. */
const LICENCES: Record<string, { url: string; name: string }> = {
  'PolyForm-Noncommercial-1.0.0': { url: 'https://polyformproject.org/licenses/noncommercial/1.0.0', name: 'PolyForm Noncommercial License 1.0.0' },
  MIT: { url: 'https://opensource.org/licenses/MIT', name: 'MIT License' },
}

/** What a chapter title reads as, without its <em>. */
function textOf(node: ReactNode): string {
  if (node == null || typeof node === 'boolean') return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(textOf).join('')
  if (isValidElement<{ children?: ReactNode }>(node)) return textOf(node.props.children)
  return ''
}

/** HTML as a reader sees it: tags dropped, entities decoded, white space collapsed. */
function plain(html: string): string {
  return html
    .replace(/<[^>]+>/g, ' ')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim()
}

const meta = (attr: 'name' | 'property', key: string) => new RegExp(`<meta ${attr}="${key}" content="([^"]*)"`).exec(indexHtml)?.[1]
const head = indexHtml.slice(0, indexHtml.indexOf('</head>'))
// The block holds no <div>, so it ends at the first </div> after it opens (index.html says so beside it).
const blockStart = indexHtml.indexOf('<div id="landing-text">')
const block = indexHtml.slice(blockStart, indexHtml.indexOf('</div>', blockStart))
const text = plain(block)

describe('the head of "/"', () => {
  it('names the page, describes it and gives its one address', () => {
    expect(head).toMatch(/<title>OpenFNO — [^<]*Indian F&amp;O<\/title>/)
    const description = meta('name', 'description') ?? ''
    expect(description.startsWith('OpenFNO is ')).toBe(true)
    expect(description.length).toBeGreaterThan(120)
    expect(head).toContain('<link rel="canonical" href="https://openfno.com/" />')
    expect(meta('name', 'robots')).toBe('index,follow,max-image-preview:large')
  })

  it('carries an Open Graph and Twitter card whose image is in the repo', () => {
    expect(meta('property', 'og:url')).toBe('https://openfno.com/')
    expect(meta('property', 'og:title')).toMatch(/^OpenFNO — /)
    expect(meta('property', 'og:description')).toBeTruthy()
    expect(meta('name', 'twitter:card')).toBe('summary_large_image')
    const image = meta('property', 'og:image') ?? ''
    expect(image).toMatch(/^https:\/\/openfno\.com\/brand\//)
    expect(meta('name', 'twitter:image')).toBe(image)
    expect(publicFiles).toContain(image.replace('https://openfno.com', ''))
  })
})

describe('the JSON-LD of "/"', () => {
  const json = /<script type="application\/ld\+json">([\s\S]*?)<\/script>/.exec(head)?.[1] ?? ''
  const graph = (JSON.parse(json) as { '@graph': Record<string, unknown>[] })['@graph']
  const node = (type: string) => graph.find((n) => n['@type'] === type) ?? {}

  it('describes the software and its source, with the GitHub repository', () => {
    expect(node('SoftwareApplication')).toMatchObject({ name: 'OpenFNO', url: 'https://openfno.com/', author: { '@id': 'https://openfno.com/#author' } })
    expect(node('SoftwareSourceCode')).toMatchObject({ codeRepository: GITHUB_URL, targetProduct: { '@id': 'https://openfno.com/#app' } })
    expect(node('Person')).toMatchObject({ '@id': 'https://openfno.com/#author', name: AUTHOR })
    expect(node('Person').sameAs).toContain(LINKEDIN_URL)
  })

  it('names the licence the repository’s LICENSE file carries, and no other', () => {
    const spdx = /SPDX-License-Identifier:\s*(\S+)/.exec(licenceFile)?.[1] ?? (/^\s*MIT License/.test(licenceFile) ? 'MIT' : '')
    const licence = LICENCES[spdx]
    expect(licence, `LICENSE names ${spdx || 'no SPDX id'}; add it to LICENCES`).toBeDefined()
    expect(node('SoftwareApplication').license).toBe(licence.url)
    expect(node('SoftwareSourceCode').license).toBe(licence.url)
    // The FAQ says it in words; it said "MIT" until 29 Oct 2026, while the file has always been PolyForm Noncommercial.
    expect(json).toContain(licence.name)
    for (const other of Object.values(LICENCES).filter((l) => l !== licence)) {
      expect(json).not.toContain(other.url)
      expect(json).not.toMatch(new RegExp(`\\b${other.name.split(' ')[0]}\\b`))
    }
  })
})

describe('the text of "/" in the HTML itself', () => {
  const hero = COPY[0]

  it('sits inside #root, where React replaces it on mount', () => {
    expect(blockStart).toBeGreaterThan(indexHtml.indexOf('<div id="root">'))
    expect(blockStart).toBeLessThan(indexHtml.indexOf('<script type="module" src="/src/main.tsx">'))
    expect(block.slice('<div id="landing-text">'.length)).not.toContain('<div')
    expect(text.split(' ').length).toBeGreaterThan(400)
  })

  it('is hidden while scripting is on and shown, instead of the loader, while it is off', () => {
    expect(indexHtml).toMatch(/<style>[\s\S]*#landing-text \{ display: none;[\s\S]*<\/style>\s*<div id="root">/)
    // One, and not named in a comment either, where a tool reading tags by pattern would take it for the real one.
    expect(head.match(/<noscript>/g)).toHaveLength(1)
    const noscript = /<noscript>([\s\S]*?)<\/noscript>/.exec(head)?.[1] ?? ''
    // :root lifts each rule above the body's own #boot and #landing-text, which come later.
    expect(noscript).toContain(':root #boot { display: none }')
    expect(noscript).toContain(':root #landing-text { display: block }')
  })

  it('opens on the homepage’s headline and what OpenFNO is', () => {
    expect(block).toContain(`<h1>${textOf(hero.title)}</h1>`)
    expect(block.match(/<h1>/g)).toHaveLength(1)
    expect(text).toContain(hero.label)
    expect(text).toContain(hero.body)
  })

  it('names the layers of the engine, in the page’s words where it says them exactly', () => {
    for (const c of COPY.slice(1, 5)) expect(block).toContain(`<h2>${c.label}</h2>`)
    for (const key of ['chain', 'risk'] as const) expect(text).toContain(COPY.find((c) => c.key === key)!.body)
    // Said more exactly than the page: which vendors carry ticks, and which strategies are lists of conditions.
    expect(text).toContain('Dhan is the main live feed')
    expect(text).toContain('Angel One is connected read only')
    expect(text).toContain('A setup you write in the console is a list of conditions')
  })

  it('shows the console’s six screens as the page captions them', () => {
    for (const s of SCREENS) expect(text).toContain(`${s.name} ${s.text}`)
  })

  it('shows the losing run with the ledger’s own figures, and says nothing has an edge', () => {
    expect(text).toContain('The only run we show in full lost money.')
    expect(text).toContain(
      `A directional strategy replayed over ${RUN.underlying} one-minute candles, ${RUN.sessions} sessions from January to September 2026, every option leg priced from stored premiums.`,
    )
    expect(text).toContain(
      `Net −${rupees(RUN.netPnl)} on a ${rupees(RUN.initialCapital)} account: ${RUN.trades} closed trades, ${RUN.winRatePct}% winners, −₹${Math.abs(RUN.perTrade)} a trade, ${RUN.profitableSessions} of ${RUN.sessions} days positive, and a deepest fall of ${rupees(RUN.maxDrawdown)}.`,
    )
    expect(text).toContain('No strategy here has shown an edge after costs, and everything runs on paper.')
    expect(text).toContain('Trading involves financial risk. OpenFNO executes on paper against live ticks; validate every strategy before you trust it with money.')
  })

  it('links to the docs, the source and its licence', () => {
    expect(block).toContain('href="/docs/"')
    expect(block).toContain(`href="${GITHUB_URL}"`)
    expect(block).toContain(`href="${LICENSE_URL}"`)
    expect(block).toContain(`href="${LINKEDIN_URL}"`)
    expect(text).toContain(`Built by ${AUTHOR}`)
  })

  it('never claims data stays put or names a licence the repository does not carry', () => {
    // The page's "No data leaves" is left out: alerts go to Telegram and the nightly archive to Google Drive.
    expect(text).not.toMatch(/no data leaves/i)
    expect(text).not.toMatch(/\bMIT\b/)
  })
})
