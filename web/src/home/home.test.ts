/**
 * The public homepage, home.html, as the server sends it to "/": plain HTML,
 * read by search engines, link previews and people before any script runs.
 * These tests hold it to what the project is and what it may say about
 * itself: the licence the repository actually carries, no "open source" claim
 * (PolyForm Noncommercial is source-available), paper trading only, no
 * strategy code names, and nothing loaded from another site (the API's CSP
 * allows 'self' only).
 */

import { describe, expect, it } from 'vitest'
// Outside web/, where a glob does not reach.
import licenceFile from '../../../LICENSE?raw'

const read = (glob: Record<string, unknown>) => Object.values(glob)[0] as string
const html = read(import.meta.glob('../../home.html', { query: '?raw', import: 'default', eager: true }))
const publicFiles = Object.keys(import.meta.glob('../../public/**/*', { query: '?url', import: 'default' })).map((k) => k.replace('../../public', ''))

/** The page's text, without tags, scripts or styles. */
const text = html
  .replace(/<script[\s\S]*?<\/script>/g, ' ')
  .replace(/<style[\s\S]*?<\/style>/g, ' ')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&amp;/g, '&')
  .replace(/\s+/g, ' ')

const meta = (attr: 'name' | 'property', key: string) => new RegExp(`<meta ${attr}="${key}" content="([^"]*)"`).exec(html)?.[1]

describe('the head of "/"', () => {
  it('names the page, describes it and gives its one address', () => {
    expect(/<title>([^<]*)<\/title>/.exec(html)?.[1]).toMatch(/not a black box/i)
    expect(meta('name', 'description')?.length ?? 0).toBeGreaterThan(80)
    expect(html).toContain('<link rel="canonical" href="https://openfno.com/">')
    expect(meta('name', 'robots')).toMatch(/^index,follow/)
  })

  it('carries Open Graph and Twitter cards whose image is in the repo', () => {
    const image = meta('property', 'og:image')
    expect(image).toBe('https://openfno.com/brand/og-image.png')
    expect(meta('name', 'twitter:image')).toBe(image)
    expect(publicFiles).toContain('/brand/og-image.png')
  })

  it('loads nothing from another site: the CSP allows only its own origin', () => {
    const srcs = [...html.matchAll(/<(?:script|link)[^>]+(?:src|href)="([^"]+)"/g)].map((m) => m[1])
    expect(srcs.filter((u) => /^https?:\/\//.test(u) && !u.startsWith('https://openfno.com/'))).toEqual([])
    expect(html).not.toMatch(/fonts\.googleapis|fonts\.gstatic/)
  })

  it('uses icons and fonts that exist in public/', () => {
    for (const path of ['/favicon.svg', '/apple-touch-icon.png', '/fonts/geist-latin.woff2']) expect(publicFiles).toContain(path)
  })
})

describe('the structured data of "/"', () => {
  const json = /<script type="application\/ld\+json">([\s\S]*?)<\/script>/.exec(html)?.[1] ?? ''
  const graph = (JSON.parse(json) as { '@graph': Record<string, unknown>[] })['@graph']
  const byType = (t: string) => graph.find((n) => n['@type'] === t)

  it('describes the software and its source, with the GitHub repository', () => {
    expect(byType('SoftwareSourceCode')?.codeRepository).toBe('https://github.com/helloupendra/algorithmic-trading-engine')
    expect(byType('SoftwareApplication')?.name).toBe('OpenFNO')
  })

  it('names the licence the repository’s LICENSE file carries', () => {
    expect(licenceFile).toContain('SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0')
    for (const t of ['SoftwareApplication', 'SoftwareSourceCode']) {
      expect(byType(t)?.license).toBe('https://polyformproject.org/licenses/noncommercial/1.0.0')
    }
  })
})

describe('the words of "/"', () => {
  it('has one headline, and it is the product’s line', () => {
    expect(html.match(/<h1[\s>]/g)).toHaveLength(1)
    expect(text).toMatch(/Not a\s+black box\./)
  })

  it('calls the project source-available, never open source', () => {
    expect(text).toMatch(/source-available/i)
    expect(text).not.toMatch(/open[- ]source/i)
    expect(json()).not.toMatch(/open[- ]source/i)
  })

  it('says it trades on paper, and names no strategy', () => {
    expect(text).toMatch(/Paper trading only|paper/i)
    expect(text).not.toMatch(/Ghost|ChainFlow|Chain Flow|SmcStructure|CrudeMomentum/)
  })

  it('shows the run that lost, with the ledger’s own figure', () => {
    expect(text).toContain('−₹1,05,391')
    expect(text).toMatch(/lost money/)
  })

  it('links to the sign-in, the docs, the source and its licence', () => {
    for (const href of ['/login', '/docs/', 'https://github.com/helloupendra/algorithmic-trading-engine', 'https://github.com/helloupendra/algorithmic-trading-engine/blob/main/LICENSE']) {
      expect(html).toContain(`href="${href}"`)
    }
    expect(html).not.toContain('href="#"')
  })
})

function json(): string {
  return /<script type="application\/ld\+json">([\s\S]*?)<\/script>/.exec(html)?.[1] ?? ''
}
