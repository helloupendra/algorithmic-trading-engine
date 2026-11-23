import { describe, expect, it } from 'vitest'
import { existsSync, readFileSync } from 'node:fs'
import { DESCRIPTIONS, FAMILIES, NAV, SITE, pageTitle, robotsTxt, sitemapXml, urlFor } from './build.mjs'

const repo = new URL('../../', import.meta.url)

describe('robots.txt', () => {
  const robots = robotsTxt()

  it('opens the homepage and the docs and points at the sitemap', () => {
    expect(robots).toMatch(/^User-agent: \*\nAllow: \/\n/)
    expect(robots).toContain(`Sitemap: ${SITE.url}/sitemap.xml\n`)
    // A bare "Disallow: /" would close the whole site; nothing under /docs is closed either.
    expect(robots).not.toMatch(/^Disallow: \/(docs.*)?$/m)
  })

  it('keeps the sign-in, the console, the API and the hubs closed', () => {
    for (const path of ['/desk', '/admin', '/trader', '/login', '/invite/', '/api/', '/hubs/']) {
      expect(robots).toContain(`Disallow: ${path}\n`)
    }
  })
})

describe('sitemap.xml', () => {
  const pages = [
    { url: `${SITE.url}/docs/`, lastmod: '2026-10-20', hasMermaid: false },
    { url: `${SITE.url}/docs/architecture/`, lastmod: '2026-10-21', hasMermaid: true },
  ]
  const xml = sitemapXml(pages, '2026-10-29')
  const entries = [...xml.matchAll(/<url><loc>([^<]+)<\/loc><lastmod>([^<]+)<\/lastmod><priority>([^<]+)<\/priority><\/url>/g)].map((m) => m.slice(1))

  it('lists the homepage first, dated by its own sources, then every docs page', () => {
    expect(entries).toEqual([
      [`${SITE.url}/`, '2026-10-29', '1.0'],
      [`${SITE.url}/docs/`, '2026-10-20', '0.9'],
      [`${SITE.url}/docs/architecture/`, '2026-10-21', '0.7'],
    ])
    expect(xml).toMatch(/^<\?xml version="1.0" encoding="UTF-8"\?>\n<urlset xmlns="http:\/\/www.sitemaps.org\/schemas\/sitemap\/0.9">\n/)
  })
})

describe('sitemap.xml and canonical pages', () => {
  it('leaves out a page that names another as its canonical', () => {
    const xml = sitemapXml(
      [
        { url: `${SITE.url}/docs/strategies/fulcrum/`, lastmod: '2026-10-20', canonical: `${SITE.url}/docs/strategies/fulcrum/` },
        { url: `${SITE.url}/docs/strategies/fulcrum-buy/`, lastmod: '2026-10-20', canonical: `${SITE.url}/docs/strategies/fulcrum/` },
      ],
      '2026-10-29',
    )
    expect(xml).toContain('/docs/strategies/fulcrum/</loc>')
    expect(xml).not.toContain('fulcrum-buy')
  })

  it('knows the Fulcrum variants as one family, and not the family page itself', () => {
    const fam = FAMILIES.find((f) => f.slug === 'strategies/fulcrum')
    expect(fam?.member.test('FulcrumMultiBuy50')).toBe(true)
    expect(fam?.member.test('Fulcrum')).toBe(false)
    expect(fam?.member.test('TrendRider')).toBe(false)
  })
})

describe('what a search result shows', () => {
  it('keeps every title within 60 characters, the longest strategy name included', () => {
    for (const it of NAV.flatMap((g) => g.items)) expect(pageTitle(it).length, pageTitle(it)).toBeLessThanOrEqual(60)
    expect(pageTitle({ title: 'FulcrumQtyAdjustmentBuy', spec: true })).toBe('FulcrumQtyAdjustmentBuy strategy spec · OpenFNO Docs')
    expect(pageTitle({ title: 'FulcrumQtyAdjustmentBuy', spec: true }).length).toBeLessThanOrEqual(60)
  })

  it('keeps every hand-written description within 160 characters, for a page that exists', () => {
    const slugs = new Set(NAV.flatMap((g) => g.items).map((it) => it.slug))
    for (const [slug, text] of Object.entries(DESCRIPTIONS)) {
      expect(text.length, slug).toBeLessThanOrEqual(160)
      // Strategy specs are found by a folder listing at build time; the others are in NAV.
      if (!slug.startsWith('strategies/')) expect(slugs.has(slug), `${slug} is not a docs page`).toBe(true)
    }
    expect(SITE.docsDescription.length).toBeLessThanOrEqual(160)
  })

  it('calls the project source-available under the licence the repository carries', () => {
    const licence = readFileSync(new URL('LICENSE', repo), 'utf8')
    expect(licence).toContain('SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0')
    expect(SITE.licence.url).toBe('https://polyformproject.org/licenses/noncommercial/1.0.0')
    for (const text of [SITE.tagline, SITE.description, SITE.docsDescription]) {
      expect(text).toMatch(/source-available/i)
      expect(text).not.toMatch(/open[- ]source/i)
    }
  })
})

describe('the homepage’s links into the docs', () => {
  it('each goes to a page this checkout builds', () => {
    // The homepage is web/home.html, the static page the API serves at "/".
    const html = readFileSync(new URL('web/home.html', repo), 'utf8')
    const links = [...new Set([...html.matchAll(/href="(\/docs\/[^"]*)"/g)].map((m) => m[1]))]
    expect(links).toContain('/docs/')
    const built = new Map(NAV.flatMap((g) => g.items).map((it) => [urlFor(it.slug), it]))
    for (const link of links) {
      const page = built.get(link)
      expect(page, `${link} is not a docs page`).toBeDefined()
      // A page whose source is not in the checkout is skipped by the build (docs/roadmap is private).
      if (page?.src) expect(existsSync(new URL(page.src, repo)), `${link}: ${page.src} is missing`).toBe(true)
    }
  })
})
