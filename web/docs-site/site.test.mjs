import { describe, expect, it } from 'vitest'
import { existsSync, readFileSync } from 'node:fs'
import { NAV, SITE, robotsTxt, sitemapXml, urlFor } from './build.mjs'

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

describe('the homepage’s links into the docs', () => {
  it('each goes to a page this checkout builds', () => {
    const html = readFileSync(new URL('web/index.html', repo), 'utf8')
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
