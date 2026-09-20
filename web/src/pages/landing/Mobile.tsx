/**
 * The homepage on a phone.
 *
 * Not the desktop page squeezed: the same story recomposed for a tall touch
 * screen. The engine (components/MachineScene) is live here too, in a lighter
 * build, and owns the upper part of the screen; the words sit where a thumb is —
 * the headline over the closed engine with the calls to action docked at the
 * bottom edge, then, as the lid comes off, a sheet that rises from the bottom
 * with one layer's copy at a time and a row of segments on top that fills as
 * the story is read (and jumps to a layer when tapped).
 *
 * The page does not tell the world numbers. It lays out two empty boxes — where
 * the closed engine should stand, and where an open layer should — and hands
 * their measured rectangles to the scene, which fits the engine inside. Turn the
 * phone on its side and the CSS moves the boxes (engine left, sheet right); the
 * scene follows without knowing why.
 *
 * Without the live world (reduced motion, no WebGL, a failed chunk) the same
 * chapters lay out in flow, each a screen tall, over portrait stills rendered
 * from this very page.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { useMarketClock } from '../../lib/marketClock'
import { liveSceneWanted } from '../../lib/sceneMode'
import { IconLogo } from '../../components/icons'
import { MachineScene } from '../../components/MachineScene'
import { Still } from '../../components/Still'
import { CHAPTERS, chapterProgress, type ChapterKey, type Rect } from '../../scene/story'
import { RUN } from '../../scene/evidence'
import type { MachineHandle } from '../../scene/machine'
import { AUTHOR, COPY, GITHUB_URL, LICENSE_URL, LINKEDIN_URL, SCREENS, TAGS, rupees, type ChapterCopy } from './content'
import { Arrow, EquityCurve, GitHubMark, NeonWordmark } from './parts'
import '../landing.css'
import './mobile.css'

const HERO = COPY[0]
const LAYER_COPY = COPY.slice(1, 6)
const END = COPY[6]
const PHONE_TAGS = TAGS.filter((t) => t.phone)

/** One layer's copy, as it appears in the bottom sheet (and, without the live world, on its own screen). */
function Panel({ c, n }: { c: ChapterCopy; n: number }) {
  return (
    <>
      <p className="lpm-panel__k"><b>{String(n).padStart(2, '0')}</b> / 05<span>{c.label}</span></p>
      <h2 className="lpm-panel__title" id={`${c.key}-title`}>{c.title}</h2>
      <p className="lpm-panel__body">{c.short}</p>
      {c.specs && (
        <dl className="lpm-specs">
          {c.specs.slice(0, 3).map(([k, v]) => (
            <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      )}
    </>
  )
}

export function LandingMobile() {
  const { isAuthenticated, isAdmin, isLoading } = useAuth()
  // See landing/Desktop: during the /me probe the CTA already reads as signed in.
  const sessionLikely = isLoading || isAuthenticated
  const consoleHref = isAuthenticated ? (isAdmin ? '/admin' : '/trader') : '/login'
  const consoleLabel = sessionLikely ? 'Go to console' : 'Open the console'

  const clock = useMarketClock()
  const [live, setLive] = useState(() => liveSceneWanted())
  const rootRef = useRef<HTMLDivElement | null>(null)
  const storyRef = useRef<HTMLDivElement | null>(null)
  const heroRef = useRef<HTMLElement | null>(null)
  const endRef = useRef<HTMLElement | null>(null)
  const sheetRef = useRef<HTMLDivElement | null>(null)
  const panelsRef = useRef<HTMLDivElement | null>(null)
  const segsRef = useRef<HTMLDivElement | null>(null)
  const dockRef = useRef<HTMLDivElement | null>(null)
  const hudRef = useRef<HTMLDivElement | null>(null)
  const closedBoxRef = useRef<HTMLDivElement | null>(null)
  const openBoxRef = useRef<HTMLDivElement | null>(null)
  const machineRef = useRef<MachineHandle | null>(null)
  const shown = useRef({ chapter: 0, panel: -1, sheet: false })

  useEffect(() => {
    const previous = document.title
    document.title = 'OpenFNO — not a black box. An open-source algo trading desk for Indian F&O'
    return () => {
      document.title = previous
    }
  }, [])

  useEffect(() => {
    const onScroll = () => {
      const root = rootRef.current
      const story = storyRef.current
      if (!root) return
      root.classList.toggle('is-scrolled', window.scrollY > 16)
      if (story) root.classList.toggle('is-past', story.getBoundingClientRect().bottom <= window.innerHeight + 2)
    }
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  /**
   * Tell the world where the engine stands: measure the two boxes the layout
   * leaves free. The boxes hang off the hero's text and the sheet, whose sizes
   * are published to CSS as custom properties first.
   */
  const place = useCallback(() => {
    const root = rootRef.current
    const m = machineRef.current
    const closed = closedBoxRef.current
    const open = openBoxRef.current
    if (!root || !closed || !open) return
    if (heroRef.current) root.style.setProperty('--hero-bottom', `${Math.round(heroRef.current.offsetTop + heroRef.current.offsetHeight)}px`)
    if (sheetRef.current) root.style.setProperty('--sheet-h', `${Math.round(sheetRef.current.offsetHeight)}px`)
    if (dockRef.current) root.style.setProperty('--dock-h', `${Math.round(dockRef.current.offsetHeight)}px`)
    if (!m) return
    const { width, height } = m.size()
    if (!width || !height) return
    const frac = (el: HTMLElement): Rect => {
      const r = el.getBoundingClientRect()
      return { x: r.left / width, y: r.top / height, w: r.width / width, h: r.height / height }
    }
    m.setStage({ closed: frac(closed), open: frac(open) })
  }, [])

  useEffect(() => {
    if (!live) return
    const ro = new ResizeObserver(place)
    for (const el of [heroRef.current, sheetRef.current, dockRef.current, closedBoxRef.current, openBoxRef.current]) if (el) ro.observe(el)
    window.addEventListener('resize', place)
    window.visualViewport?.addEventListener('resize', place)
    return () => {
      ro.disconnect()
      window.removeEventListener('resize', place)
      window.visualViewport?.removeEventListener('resize', place)
    }
  }, [live, place])

  const onReady = useCallback((m: MachineHandle) => {
    machineRef.current = m
    place()
  }, [place])

  const goTo = useCallback((key: ChapterKey) => {
    const story = storyRef.current
    if (!story) return
    if (!live) {
      document.getElementById(key)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      return
    }
    const top = story.getBoundingClientRect().top + window.scrollY
    window.scrollTo({ top: top + chapterProgress(key) * (story.offsetHeight - window.innerHeight), behavior: 'smooth' })
  }, [live])

  /** After each rendered frame: which words are up, how far the segments have filled, where the labels go. */
  const onFrame = useCallback((m: MachineHandle) => {
    const st = m.state()
    const nearest = Math.round(st.u)
    const chapter = Math.abs(st.u - nearest) < 0.36 ? nearest : -1
    // The sheet stays up across the five layers; only its panel changes.
    const sheetUp = st.u > 0.62 && st.u < 5.38
    const panel = sheetUp ? Math.min(4, Math.max(0, nearest - 1)) : -1
    const was = shown.current
    if (chapter !== was.chapter || panel !== was.panel || sheetUp !== was.sheet) {
      shown.current = { chapter, panel, sheet: sheetUp }
      const hero = heroRef.current
      const end = endRef.current
      if (hero) { hero.classList.toggle('is-on', chapter === 0); hero.inert = chapter !== 0 }
      if (end) { end.classList.toggle('is-on', chapter === 6); end.inert = chapter !== 6 }
      sheetRef.current?.classList.toggle('is-up', sheetUp)
      if (sheetRef.current) sheetRef.current.inert = !sheetUp
      dockRef.current?.classList.toggle('is-on', !sheetUp)
      if (dockRef.current) dockRef.current.inert = sheetUp
      const panels = panelsRef.current?.children
      if (panels) {
        for (let i = 0; i < panels.length; i++) {
          const el = panels[i] as HTMLElement
          el.classList.toggle('is-on', i === panel)
          el.inert = i !== panel
        }
      }
    }
    const segs = segsRef.current?.children
    if (segs) {
      const t = st.u - 0.5
      for (let i = 0; i < segs.length; i++) {
        const fill = Math.min(1, Math.max(0, t - i))
        ;((segs[i] as HTMLElement).firstElementChild as HTMLElement).style.transform = `scaleX(${fill.toFixed(3)})`
      }
    }
    const hud = hudRef.current
    if (hud) {
      const kids = hud.children
      for (let i = 0; i < kids.length; i++) {
        const el = kids[i] as HTMLElement
        const tag = PHONE_TAGS[i]
        const at = CHAPTERS.findIndex((c) => c.key === tag.chapter)
        const a = m.project(tag.anchor)
        const opacity = a.visible ? Math.max(0, Math.min(1, 1 - Math.abs(st.u - at) * 2.2)) : 0
        el.style.opacity = opacity.toFixed(2)
        if (opacity > 0) el.style.transform = `translate(${a.x.toFixed(1)}px, ${a.y.toFixed(1)}px)`
      }
    }
  }, [])

  const actions = (
    <>
      <Link className="cta cta--solid" to={consoleHref}>{consoleLabel} <Arrow /></Link>
      <a className="cta cta--line lpm-icon-cta" href={GITHUB_URL} target="_blank" rel="noopener noreferrer" aria-label="Read the source on GitHub"><GitHubMark /></a>
    </>
  )

  return (
    <div className={`lp lpm ${live ? 'lpm--live' : 'lpm--still'}`} ref={rootRef}>
      <header className="lpm-top">
        <a className="brand" href="#top" aria-label="OpenFNO home">
          <span className="brand__mark"><IconLogo /></span>
          <span className="brand__word">openfno</span>
        </a>
        <span className={`market${clock.open ? ' is-open' : ''}`} title="NSE cash timings, holidays aside"><i aria-hidden="true" />NSE {clock.open ? 'open' : 'closed'}</span>
        <Link className="cta cta--solid cta--sm" to={consoleHref}>Console</Link>
      </header>

      <main id="top">
        <div className="lpm-story" ref={storyRef}>
          {live && (
            <>
              <MachineScene mode="story" trackRef={storyRef} onReady={onReady} onFrame={onFrame} onUnavailable={() => setLive(false)} />

              {/* Where the engine stands: laid out by CSS, measured by place(), never drawn. */}
              <div className="lpm-box lpm-box--closed" ref={closedBoxRef} aria-hidden="true" />
              <div className="lpm-box lpm-box--open" ref={openBoxRef} aria-hidden="true" />

              <div className="lpm-hud" ref={hudRef} aria-hidden="true">
                {PHONE_TAGS.map((t) => (
                  <span key={t.anchor} className={`tag${t.tone ? ` tag--${t.tone}` : ''}`}>{t.text}</span>
                ))}
              </div>

              <section className="lpm-hero is-on" ref={heroRef} aria-labelledby="hero-title">
                <p className="eyebrow"><i aria-hidden="true" />Open-source · Indian F&amp;O · self-hosted</p>
                <h1 className="lpm-hero__title" id="hero-title">Not a{' '}<br /><em>black box.</em></h1>
                <p className="lpm-hero__body">{HERO.short}</p>
              </section>

              <section className="lpm-hero lpm-hero--end" ref={endRef} aria-labelledby="open-title" inert>
                <p className="eyebrow"><i aria-hidden="true" />{END.label}</p>
                <h2 className="lpm-hero__title" id="open-title">Read{' '}<br /><em>every line.</em></h2>
                <p className="lpm-hero__body">{END.short}</p>
              </section>

              <div className="lpm-sheet" ref={sheetRef} inert>
                <div className="lpm-sheet__grip" aria-hidden="true" />
                <div className="lpm-segs" ref={segsRef} role="group" aria-label="Layers of the engine">
                  {LAYER_COPY.map((c) => (
                    <button key={c.key} type="button" onClick={() => goTo(c.key)} aria-label={c.label}><i /></button>
                  ))}
                </div>
                <div className="lpm-panels" ref={panelsRef}>
                  {LAYER_COPY.map((c, i) => (
                    <section key={c.key} className="lpm-panel" aria-labelledby={`${c.key}-title`} inert>
                      <Panel c={c} n={i + 1} />
                    </section>
                  ))}
                </div>
              </div>

              <div className="lpm-dock is-on" ref={dockRef}>
                <p className="lpm-dock__cue" aria-hidden="true"><span />swipe up to take the lid off</p>
                <div className="lpm-dock__row">{actions}</div>
              </div>

              <div className="lpm-story__length" aria-hidden="true" />
            </>
          )}

          {!live && (
            <div className="lpm-stills">
              <section className="lpm-still lpm-still--closed" id="hero" aria-labelledby="hero-title">
                <Still pose="hero" className="lpm-still__bg" />
                <div className="lpm-still__top">
                  <p className="eyebrow"><i aria-hidden="true" />Open-source · Indian F&amp;O · self-hosted</p>
                  <h1 className="lpm-hero__title" id="hero-title">Not a{' '}<br /><em>black box.</em></h1>
                  <p className="lpm-hero__body">{HERO.short}</p>
                </div>
                <div className="lpm-dock__row lpm-still__dock">{actions}</div>
              </section>
              {LAYER_COPY.map((c, i) => (
                <section key={c.key} className="lpm-still" id={c.key} aria-labelledby={`${c.key}-title`}>
                  <Still pose={c.key} className="lpm-still__bg" />
                  <div className="lpm-still__sheet"><Panel c={c} n={i + 1} /></div>
                </section>
              ))}
              <section className="lpm-still lpm-still--closed" id="open" aria-labelledby="open-title">
                <Still pose="open" className="lpm-still__bg" />
                <div className="lpm-still__top">
                  <p className="eyebrow"><i aria-hidden="true" />{END.label}</p>
                  <h2 className="lpm-hero__title" id="open-title">Read{' '}<br /><em>every line.</em></h2>
                  <p className="lpm-hero__body">{END.short}</p>
                </div>
                <div className="lpm-dock__row lpm-still__dock">{actions}</div>
              </section>
            </div>
          )}
        </div>

        {/* ------------------------------------------------------- console */}
        <section id="console" className="lpm-band" aria-labelledby="console-title">
          <p className="eyebrow"><i aria-hidden="true" />The console</p>
          <h2 id="console-title">Six screens. <em>No mock-ups.</em></h2>
          <p className="lpm-band__lead">Captures of this deployment, as it ran. Swipe sideways.</p>
          <div className="lpm-reel" tabIndex={0} role="group" aria-label="Console screens">
            {SCREENS.map((s, i) => (
              <figure className="lpm-reel__card" key={s.name}>
                <div className="lpm-reel__frame"><img src={s.src} width={s.w} height={s.h} alt={s.alt} loading="lazy" decoding="async" /></div>
                <figcaption>
                  <span className="lpm-reel__n">{String(i + 1).padStart(2, '0')} / 06</span>
                  <b>{s.name}</b>
                  <span>{s.text}</span>
                </figcaption>
              </figure>
            ))}
          </div>
        </section>

        {/* --------------------------------------------------------- proof */}
        <section id="proof" className="lpm-band" aria-labelledby="proof-title">
          <p className="eyebrow"><i aria-hidden="true" />Proof, not promises</p>
          <h2 id="proof-title">The only run we show in full <em>lost money.</em></h2>
          <p className="lpm-band__lead">
            A directional strategy replayed over {RUN.underlying} one-minute candles, {RUN.sessions} sessions from January to
            September 2026, every option leg priced from stored premiums with brokerage and slippage applied.
          </p>
          <p className="lpm-figure"><b>−{rupees(RUN.netPnl)}</b><span>net, on a {rupees(RUN.initialCapital)} account</span></p>
          <EquityCurve />
          <dl className="lpm-stats">
            <div><dt>closed trades</dt><dd>{RUN.trades}</dd></div>
            <div><dt>winners</dt><dd>{RUN.winRatePct}%</dd></div>
            <div><dt>a trade</dt><dd>−₹{Math.abs(RUN.perTrade)}</dd></div>
            <div><dt>days positive</dt><dd>{RUN.profitableSessions} / {RUN.sessions}</dd></div>
            <div><dt>deepest fall</dt><dd>{rupees(RUN.maxDrawdown)}</dd></div>
            <div><dt>lowest balance</dt><dd>{rupees(RUN.lowestEquity)}</dd></div>
          </dl>
        </section>

        {/* -------------------------------------------------------- run it */}
        <section id="run" className="lpm-band" aria-labelledby="run-title">
          <p className="eyebrow"><i aria-hidden="true" />Yours</p>
          <h2 id="run-title">Your machine. <em>Your keys.</em></h2>
          <p className="lpm-band__lead">The whole desk runs on hardware you control. No data leaves, and no strategy is uploaded anywhere.</p>
          <div className="lpm-term" role="group" aria-label="Quick start">
            <div className="lpm-term__bar" aria-hidden="true"><i /><i /><i /><span>quick start</span></div>
            <pre tabIndex={0}><code>
              <span className="c"># 1 · clone and bootstrap</span>{'\n'}
              <span className="p">$</span> git clone {GITHUB_URL}.git{'\n'}
              <span className="p">$</span> cd algorithmic-trading-engine{'\n'}
              <span className="p">$</span> ./scripts/setup.sh{'\n\n'}
              <span className="c"># 2 · start the API</span>{'\n'}
              <span className="p">$</span> dotnet run --project src/AlgoTrading.Api{'\n\n'}
              <span className="c"># 3 · load reference data</span>{'\n'}
              <span className="p">$</span> ./scripts/load-data.sh
            </code></pre>
          </div>
          <div className="lpm-stack-cta">
            <a className="cta cta--solid" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Read the source</a>
            <a className="cta cta--line" href="/docs/">Read the docs <Arrow /></a>
          </div>
        </section>
      </main>

      <footer className="lpm-foot">
        <NeonWordmark />
        <p className="lpm-foot__by">Built by {AUTHOR}</p>
        <p className="lpm-foot__links">
          <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer">GitHub</a>
          <a href={LINKEDIN_URL} target="_blank" rel="noopener noreferrer">LinkedIn</a>
          <a href="/docs/">Docs</a>
          <a href={LICENSE_URL} target="_blank" rel="noopener noreferrer">Licence</a>
          <Link to={consoleHref}>Console</Link>
        </p>
        <p className="lpm-foot__risk">Trading involves financial risk. OpenFNO executes on paper against live ticks; validate every strategy before you trust it with money.</p>
      </footer>
    </div>
  )
}
