/**
 * Static documentation site — docs/*.md → dist/docs/**, plus robots.txt and
 * sitemap.xml for the whole site.
 *
 * Why static: the console is a client-rendered app behind a sign-in, which
 * is the wrong shape for a page that has to be read by a search engine, a
 * recruiter on a phone, or someone with JavaScript off. Every doc becomes
 * real HTML with its own title, description, canonical URL, Open Graph card
 * and JSON-LD, generated at build time and served by the API next to the
 * console. No runtime, no framework: a reader gets a page.
 *
 * Runs from the Vite build (see vite.config.ts) so the desk's `vite build`
 * publishes the docs with the console — nothing extra to remember.
 */
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { Marked } from 'marked'
import katex from 'katex'

const here = path.dirname(fileURLToPath(import.meta.url))
const repo = path.resolve(here, '..', '..')

export const SITE = {
  url: 'https://openfno.com',
  name: 'OpenFNO',
  tagline: 'Open-source algorithmic trading for Indian F&O',
  github: 'https://github.com/helloupendra/algorithmic-trading-engine',
  description:
    'OpenFNO is an open-source algorithmic trading platform for Indian F&O: live paper runs on real ticks, coverage-first backtests, options strategies with position-level risk, and a console for operators and traders.',
}

/** The navigation, in reading order. `src` is repo-relative; `slug` is the URL under /docs/. */
const NAV = [
  {
    section: 'Start here',
    items: [
      { slug: '', title: 'Overview', index: true },
      { slug: 'architecture', src: 'docs/01_ARCHITECTURE_OVERVIEW.md', title: 'Architecture overview' },
      { slug: 'research', src: 'docs/RESEARCH_AND_ARCHITECTURE.md', title: 'Research & architecture' },
      { slug: 'risk-management', src: 'docs/03_ARCHITECTURE_AND_RISK_MANAGEMENT.md', title: 'Risk management' },
      { slug: 'status', src: 'docs/PROJECT_STATUS.md', title: 'Project status' },
    ],
  },
  {
    section: 'Modules',
    items: [
      { slug: 'modules/data', src: 'docs/modules/data_module.md', title: 'Data' },
      { slug: 'modules/strategies', src: 'docs/modules/strategies_module.md', title: 'Strategies & live runner' },
      { slug: 'modules/backtesting', src: 'docs/modules/backtesting_module.md', title: 'Backtesting' },
      { slug: 'modules/option-chain', src: 'docs/modules/option_chain.md', title: 'Option chain' },
      { slug: 'modules/connectors', src: 'docs/modules/connectors_module.md', title: 'Connectors' },
      { slug: 'modules/users', src: 'docs/modules/users_module.md', title: 'Users & access' },
      { slug: 'modules/strategy-packages', src: 'docs/modules/strategy_packages.md', title: 'Strategy packages' },
      { slug: 'modules/activity-log', src: 'docs/modules/activity_log.md', title: 'Activity log' },
    ],
  },
  {
    section: 'Strategies',
    items: [
      { slug: 'strategies', src: 'docs/strategies/README.md', title: 'Strategy specifications' },
      { slug: 'strategies/ghost-tangent-crossings', src: 'docs/strategies/GhostTangentCrossings.md', title: 'GhostTangentCrossings' },
    ],
  },
  {
    section: 'Run it',
    items: [
      { slug: 'deploy/local', src: 'docs/02_LOCAL_DEPLOYMENT_GUIDE.md', title: 'Local deployment' },
      { slug: 'deploy/aws', src: 'scripts/aws/README.md', title: 'Deploying to AWS' },
    ],
  },
  {
    section: 'Roadmap',
    items: [
      { slug: 'roadmap/broker-and-data-providers', src: 'docs/roadmap/broker-and-data-provider-module.md', title: 'Brokers & data providers' },
      { slug: 'roadmap/remaining-scope', src: 'docs/roadmap/remaining-scope-risk-alerts-users-trader.md', title: 'Remaining scope' },
      { slug: 'roadmap/batch-b-review', src: 'docs/roadmap/batch-b-review-fixes.md', title: 'Batch B review fixes' },
    ],
  },
]

/** Screenshots on the overview page, with what each shows. */
const GALLERY = [
  ['console-admin-home.png', 'Operator home — market, broker, feed and every module at a glance'],
  ['console-trader-home.png', 'Trader home — indices, large caps, commodities, then their own runs'],
  ['console-live-runner.png', 'Live runner — strategies running on real ticks, paper-filled'],
  ['console-live-run-detail.png', 'Run detail — every leg with entry, exit, lots × lot size and P&L'],
  ['console-backtesting.png', 'Backtesting — coverage first, so a replay never runs on missing data'],
  ['console-strategies-live.png', 'Strategy library — the rules, the contracts, and How it works'],
  ['console-live-feeds.png', 'Live feeds — what the ingestor carries and how fresh it is'],
  ['console-connectors.png', 'Connectors — brokers and data vendors behind one seam'],
  ['console-users.png', 'Users — roles, module grants and strategy packages'],
  ['console-activity-log.png', 'Activity log — who did what, across every module'],
]

/* ------------------------------------------------------------------ utils */

const esc = (s) =>
  String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

function slugify(text) {
  return String(text)
    .toLowerCase()
    .replace(/<[^>]+>/g, '')
    .replace(/&[a-z]+;/g, '')
    .replace(/[^a-z0-9\s-]/g, '')
    .trim()
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .slice(0, 80)
}

function gitDate(file) {
  try {
    const out = execFileSync('git', ['log', '-1', '--format=%cI', '--', file], { cwd: repo, encoding: 'utf8' }).trim()
    if (out) return out.slice(0, 10)
  } catch {
    /* not a git checkout: fall back to today */
  }
  return new Date().toISOString().slice(0, 10)
}

/** Plain text of the first real paragraph — the page's description. */
function firstParagraph(md) {
  const lines = md.split('\n')
  let i = 0
  while (i < lines.length && (lines[i].trim() === '' || /^#/.test(lines[i]) || /^!\[/.test(lines[i]) || /^<!--/.test(lines[i]) || /^\|/.test(lines[i]) || /^[-*>]/.test(lines[i]) || /^```/.test(lines[i]))) i++
  const para = []
  while (i < lines.length && lines[i].trim() !== '' && !/^#/.test(lines[i]) && !/^```/.test(lines[i])) para.push(lines[i++])
  const text = para
    .join(' ')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_>]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
  return text.length > 158 ? text.slice(0, 155).replace(/\s+\S*$/, '') + '…' : text
}

/* ------------------------------------------------------------- markdown */

/** `$$…$$` and inline `$…$` rendered by KaTeX before Markdown sees them, outside code. */
function renderMath(md) {
  if (!md.includes('$$')) return md
  const parts = md.split(/(```[\s\S]*?```|`[^`\n]*`)/g)
  return parts
    .map((part, i) => {
      if (i % 2 === 1) return part
      return part
        .replace(/\$\$([\s\S]+?)\$\$/g, (_, tex) => {
          try {
            return `\n\n<div class="math">${katex.renderToString(tex.trim(), { displayMode: true, throwOnError: false })}</div>\n\n`
          } catch {
            return _
          }
        })
        .replace(/(^|[^\\$])\$([^\s$][^$\n]*?[^\s$]|[^\s$])\$(?![\d$])/g, (m, pre, tex) => {
          try {
            return pre + katex.renderToString(tex, { throwOnError: false })
          } catch {
            return m
          }
        })
    })
    .join('')
}

function makeMarked(page, slugToUrl) {
  const marked = new Marked({ gfm: true, breaks: false })
  const seen = new Map()
  const toc = []
  marked.use({
    renderer: {
      heading({ tokens, depth }) {
        const html = this.parser.parseInline(tokens)
        let id = slugify(html) || `section-${toc.length + 1}`
        const n = seen.get(id) ?? 0
        seen.set(id, n + 1)
        if (n > 0) id = `${id}-${n}`
        // The document's H1 is the page title, rendered by the layout.
        if (depth === 1) return ''
        // Already HTML-escaped by the inline parser; tags are dropped, entities kept.
        if (depth <= 3) toc.push({ depth, id, text: html.replace(/<[^>]+>/g, '') })
        return `<h${depth} id="${id}"><a class="anchor" href="#${id}" aria-label="Link to this section">#</a>${html}</h${depth}>\n`
      },
      code({ text, lang }) {
        if (lang === 'mermaid') {
          page.hasMermaid = true
          return `<pre class="mermaid">${esc(text)}</pre>\n`
        }
        const cls = lang ? ` class="language-${esc(lang)}"` : ''
        return `<pre><code${cls}>${esc(text)}</code></pre>\n`
      },
      table({ header, rows }) {
        const th = header.map((c) => `<th${c.align ? ` style="text-align:${c.align}"` : ''}>${this.parser.parseInline(c.tokens)}</th>`).join('')
        const body = rows
          .map((r) => `<tr>${r.map((c) => `<td${c.align ? ` style="text-align:${c.align}"` : ''}>${this.parser.parseInline(c.tokens)}</td>`).join('')}</tr>`)
          .join('')
        return `<div class="tablewrap"><table><thead><tr>${th}</tr></thead><tbody>${body}</tbody></table></div>\n`
      },
      image({ href, title, text }) {
        // Badges from shields.io are README furniture, not documentation, and
        // the site's content-security policy would refuse them anyway.
        if (/shields\.io|badge/.test(href)) return ''
        let src = href
        if (!/^https?:/.test(href)) {
          const base = path.posix.dirname(page.src.replace(/\\/g, '/'))
          const rel = path.posix.normalize(path.posix.join(base, href))
          src = rel.startsWith('docs/') ? `/docs/${rel.slice(5)}` : `${SITE.github}/blob/main/${rel}?raw=true`
        }
        return `<figure><img src="${esc(src)}" alt="${esc(text || '')}" loading="lazy"${title ? ` title="${esc(title)}"` : ''}>${text ? `<figcaption>${esc(text)}</figcaption>` : ''}</figure>`
      },
      link({ href, title, tokens }) {
        const inner = this.parser.parseInline(tokens)
        let url = href
        let external = /^https?:/.test(href)
        if (!external && !href.startsWith('#') && !href.startsWith('mailto:')) {
          const base = path.posix.dirname(page.src.replace(/\\/g, '/'))
          const [file, hash] = href.split('#')
          const rel = path.posix.normalize(path.posix.join(base, file))
          const target = slugToUrl.get(rel)
          if (target) url = `${target}${hash ? `#${hash}` : ''}`
          else {
            url = `${SITE.github}/blob/main/${rel}`
            external = true
          }
        }
        const attrs = external ? ' target="_blank" rel="noopener"' : ''
        return `<a href="${esc(url)}"${title ? ` title="${esc(title)}"` : ''}${attrs}>${inner}</a>`
      },
    },
  })
  return { marked, toc }
}

/* -------------------------------------------------------------- layout */

function jsonLd(obj) {
  return `<script type="application/ld+json">${JSON.stringify(obj).replace(/</g, '\\u003c')}</script>`
}

function navHtml(current) {
  return NAV.map(
    (g) => `<div class="nav__group"><div class="nav__label">${esc(g.section)}</div>${g.items
      .map((it) => `<a class="nav__item${it.slug === current ? ' is-active' : ''}" href="${urlFor(it.slug)}"${it.slug === current ? ' aria-current="page"' : ''}>${esc(it.title)}</a>`)
      .join('')}</div>`,
  ).join('')
}

const urlFor = (slug) => (slug ? `/docs/${slug}/` : '/docs/')

function layout({ page, body, toc, prev, next, description, dateModified }) {
  const url = `${SITE.url}${urlFor(page.slug)}`
  const title = page.index ? `${SITE.name} Docs — ${SITE.tagline}` : `${page.title} · ${SITE.name} Docs`
  const crumbs = [{ name: 'Docs', url: `${SITE.url}/docs/` }]
  if (!page.index) crumbs.push({ name: page.title, url })
  const tocHtml =
    toc.length > 1
      ? `<nav class="toc" aria-label="On this page"><div class="toc__title">On this page</div><ol>${toc
          .map((t) => `<li class="toc__l${t.depth}"><a href="#${t.id}">${t.text}</a></li>`)
          .join('')}</ol></nav>`
      : ''
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>
<meta name="description" content="${esc(description)}">
<link rel="canonical" href="${url}">
<meta name="robots" content="index,follow,max-image-preview:large">
<meta name="theme-color" content="#070b11">
<meta property="og:type" content="article">
<meta property="og:site_name" content="${SITE.name}">
<meta property="og:title" content="${esc(page.index ? SITE.name + ' Docs' : page.title)}">
<meta property="og:description" content="${esc(description)}">
<meta property="og:url" content="${url}">
<meta property="og:image" content="${SITE.url}/brand/og-image.png">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="${esc(page.index ? SITE.name + ' Docs' : page.title)}">
<meta name="twitter:description" content="${esc(description)}">
<meta name="twitter:image" content="${SITE.url}/brand/og-image.png">
<link rel="icon" type="image/svg+xml" href="/favicon.svg">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<link rel="stylesheet" href="/docs/assets/docs.css">
${page.hasMath ? '<link rel="stylesheet" href="/docs/assets/katex/katex.min.css">' : ''}
${jsonLd({
  '@context': 'https://schema.org',
  '@type': page.index ? 'CollectionPage' : 'TechArticle',
  headline: page.index ? `${SITE.name} documentation` : page.title,
  description,
  url,
  dateModified,
  inLanguage: 'en',
  isPartOf: { '@type': 'WebSite', name: SITE.name, url: SITE.url },
  author: { '@type': 'Organization', name: SITE.name, url: SITE.url },
  publisher: { '@type': 'Organization', name: SITE.name, url: SITE.url, logo: { '@type': 'ImageObject', url: `${SITE.url}/brand/openfno-mark-512.png` } },
})}
${jsonLd({
  '@context': 'https://schema.org',
  '@type': 'BreadcrumbList',
  itemListElement: [{ '@type': 'ListItem', position: 1, name: 'Home', item: SITE.url }, ...crumbs.map((c, i) => ({ '@type': 'ListItem', position: i + 2, name: c.name, item: c.url }))],
})}
</head>
<body class="docs">
<a class="skip" href="#content">Skip to content</a>
<header class="top">
  <div class="top__wrap">
    <a class="brand" href="/" aria-label="${SITE.name} home"><img src="/brand/openfno-mark.svg" alt="" width="26" height="26"><span>open<b>fno</b></span></a>
    <span class="top__sep" aria-hidden="true">/</span>
    <a class="top__docs" href="/docs/">Docs</a>
    <button class="top__menu" type="button" aria-label="Menu" aria-controls="sidenav" aria-expanded="false">☰</button>
    <nav class="top__links" aria-label="Site">
      <a href="/">Home</a>
      <a href="${SITE.github}" target="_blank" rel="noopener">GitHub</a>
      <a class="top__cta" href="/login">Open the console</a>
    </nav>
  </div>
</header>
<div class="shell">
  <aside class="side" id="sidenav"><nav aria-label="Documentation">${navHtml(page.slug)}</nav></aside>
  <main class="main" id="content">
    <nav class="crumbs" aria-label="Breadcrumb"><a href="/">Home</a><span>/</span><a href="/docs/">Docs</a>${page.index ? '' : `<span>/</span><span aria-current="page">${esc(page.title)}</span>`}</nav>
    <article class="doc">
      ${page.index ? '' : `<h1>${esc(page.title)}</h1><p class="doc__meta">Updated ${dateModified} · <a href="${SITE.github}/blob/main/${esc(page.src)}" target="_blank" rel="noopener">Edit on GitHub</a></p>`}
      ${body}
    </article>
    <nav class="pager" aria-label="Previous and next">
      ${prev ? `<a class="pager__prev" href="${urlFor(prev.slug)}"><small>Previous</small>${esc(prev.title)}</a>` : '<span></span>'}
      ${next ? `<a class="pager__next" href="${urlFor(next.slug)}"><small>Next</small>${esc(next.title)}</a>` : '<span></span>'}
    </nav>
    <footer class="foot">
      <span>© ${new Date().getFullYear()} ${SITE.name} · open source, MIT</span>
      <span><a href="${SITE.github}" target="_blank" rel="noopener">Source</a> · <a href="/docs/deploy/local/">Run it yourself</a></span>
    </footer>
  </main>
  ${tocHtml ? `<aside class="aside">${tocHtml}</aside>` : ''}
</div>
${page.hasMermaid ? '<script src="/docs/assets/mermaid.min.js" defer></script>' : ''}
<script src="/docs/assets/docs.js" defer></script>
</body>
</html>
`
}

function indexBody() {
  const cards = NAV.flatMap((g) => g.items.filter((it) => !it.index).map((it) => ({ ...it, section: g.section })))
  const featured = [
    ['architecture', 'How the pieces fit: a .NET control plane, a Python engine, TimescaleDB, Redis, a React console.'],
    ['modules/strategies', 'The live runner: lots × lot size, three levels of risk rules, one stop pipeline.'],
    ['modules/backtesting', 'Coverage first — a replay never runs on data that is not there.'],
    ['strategies/ghost-tangent-crossings', 'A strategy spec with the maths, the exits, and a worked example from a real run.'],
    ['deploy/local', 'Run the whole platform on your machine in an afternoon.'],
    ['research', 'The research behind the design: what was tried, what was measured, what was kept.'],
  ]
  return `
<section class="hero">
  <p class="hero__kicker">Documentation</p>
  <h1>${SITE.tagline}.</h1>
  <p class="hero__lead">${esc(SITE.description)} These pages are the design, the modules, the strategy specifications and the operating manual — written for the engineer who will run it and the trader who will trust it.</p>
  <div class="hero__actions">
    <a class="btn btn--primary" href="/docs/architecture/">Start with the architecture</a>
    <a class="btn" href="${SITE.github}" target="_blank" rel="noopener">Source on GitHub</a>
  </div>
</section>

<section class="cards" aria-label="Featured">
  ${featured
    .map(([slug, blurb]) => {
      const it = cards.find((c) => c.slug === slug)
      return `<a class="card" href="${urlFor(slug)}"><span class="card__section">${esc(it.section)}</span><span class="card__title">${esc(it.title)}</span><span class="card__blurb">${esc(blurb)}</span></a>`
    })
    .join('')}
</section>

<section class="gallery" aria-label="What it looks like">
  <h2 id="what-it-looks-like">What it looks like</h2>
  <p>The console, as it runs today. Operators see everything; traders see their own runs, the market, and their own broker.</p>
  <div class="gallery__grid">
    ${GALLERY.map(([file, caption]) => `<figure><a href="/docs/image/${file}" target="_blank" rel="noopener"><img src="/docs/image/${file}" alt="${esc(caption)}" loading="lazy" width="1440" height="900"></a><figcaption>${esc(caption)}</figcaption></figure>`).join('')}
  </div>
</section>

<section class="all" aria-label="All pages">
  <h2 id="all-pages">All pages</h2>
  ${NAV.map((g) => `<h3>${esc(g.section)}</h3><ul>${g.items.filter((it) => !it.index).map((it) => `<li><a href="${urlFor(it.slug)}">${esc(it.title)}</a></li>`).join('')}</ul>`).join('')}
</section>
`
}

/* ------------------------------------------------------------------ main */

export async function buildDocs({ outDir }) {
  const docsOut = path.join(outDir, 'docs')
  await fs.rm(docsOut, { recursive: true, force: true })
  await fs.mkdir(path.join(docsOut, 'assets'), { recursive: true })

  const flat = NAV.flatMap((g) => g.items)
  const slugToUrl = new Map(flat.filter((it) => it.src).map((it) => [it.src.replace(/\\/g, '/'), urlFor(it.slug)]))
  // The repo README is linked from a few docs; it has no page here.
  slugToUrl.set('README.md', `${SITE.github}#readme`)

  const pages = []
  for (let i = 0; i < flat.length; i++) {
    const page = { ...flat[i], hasMermaid: false, hasMath: false }
    let body
    let toc = []
    let description
    let dateModified
    if (page.index) {
      body = indexBody()
      description = SITE.description
      dateModified = new Date().toISOString().slice(0, 10)
    } else {
      const raw = await fs.readFile(path.join(repo, page.src), 'utf8')
      const withMath = renderMath(raw)
      page.hasMath = withMath !== raw
      const { marked, toc: t } = makeMarked(page, slugToUrl)
      body = await marked.parse(withMath)
      toc = t
      description = firstParagraph(raw) || `${page.title} — ${SITE.name} documentation.`
      dateModified = gitDate(page.src)
    }
    const prev = i > 0 ? flat[i - 1] : null
    const next = i < flat.length - 1 ? flat[i + 1] : null
    const html = layout({ page, body, toc, prev, next, description, dateModified })
    const dir = page.slug ? path.join(docsOut, page.slug) : docsOut
    await fs.mkdir(dir, { recursive: true })
    await fs.writeFile(path.join(dir, 'index.html'), html)
    pages.push({ url: `${SITE.url}${urlFor(page.slug)}`, lastmod: dateModified, hasMermaid: page.hasMermaid })
  }

  // Assets: stylesheet, behaviour, KaTeX, mermaid, the screenshots.
  await fs.copyFile(path.join(here, 'docs.css'), path.join(docsOut, 'assets', 'docs.css'))
  await fs.copyFile(path.join(here, 'docs.js'), path.join(docsOut, 'assets', 'docs.js'))
  const katexDist = path.dirname(fileURLToPath(import.meta.resolve('katex/dist/katex.min.css')))
  await fs.mkdir(path.join(docsOut, 'assets', 'katex'), { recursive: true })
  await fs.copyFile(path.join(katexDist, 'katex.min.css'), path.join(docsOut, 'assets', 'katex', 'katex.min.css'))
  await fs.cp(path.join(katexDist, 'fonts'), path.join(docsOut, 'assets', 'katex', 'fonts'), { recursive: true })
  if (pages.some((p) => p.hasMermaid)) {
    const mermaid = fileURLToPath(import.meta.resolve('mermaid/dist/mermaid.min.js'))
    await fs.copyFile(mermaid, path.join(docsOut, 'assets', 'mermaid.min.js'))
  }
  await fs.cp(path.join(repo, 'docs', 'image'), path.join(docsOut, 'image'), { recursive: true })

  // Site-wide: robots and the sitemap. The console itself is behind a sign-in
  // and is asked not to be indexed; the landing page and the docs are.
  await fs.writeFile(
    path.join(outDir, 'robots.txt'),
    `User-agent: *\nAllow: /\nDisallow: /admin\nDisallow: /trader\nDisallow: /login\nDisallow: /invite/\nDisallow: /api/\nDisallow: /hubs/\n\nSitemap: ${SITE.url}/sitemap.xml\n`,
  )
  const today = new Date().toISOString().slice(0, 10)
  const urls = [{ url: `${SITE.url}/`, lastmod: today, priority: '1.0' }, ...pages.map((p) => ({ ...p, priority: p.url.endsWith('/docs/') ? '0.9' : '0.7' }))]
  await fs.writeFile(
    path.join(outDir, 'sitemap.xml'),
    `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls
      .map((u) => `  <url><loc>${u.url}</loc><lastmod>${u.lastmod}</lastmod><priority>${u.priority}</priority></url>`)
      .join('\n')}\n</urlset>\n`,
  )
  return pages.length
}

// Direct run: node docs-site/build.mjs [outDir]
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const outDir = path.resolve(process.argv[2] ?? path.join(here, '..', 'dist'))
  buildDocs({ outDir }).then((n) => console.log(`docs: ${n} pages → ${outDir}/docs`))
}
