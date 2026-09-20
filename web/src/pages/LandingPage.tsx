/**
 * Public homepage ("/"): not a black box.
 *
 * One object carries the page — the engine in its glass case
 * (components/MachineScene → scene/machine.ts). It arrives as a black box and
 * clears to glass; scrolling takes the lid off, separates the engine into its
 * five layers and climbs them one at a time: data, chain, strategy, risk,
 * ledger. Each layer has one chapter of copy, set like a datasheet, on the side
 * of the screen the engine is not. At the end the case closes again.
 *
 * After the story the page is flat and factual: the console's real screens,
 * the one run the site shows in full (the losing one, drawn from
 * scene/evidence.ts), and how to run it yourself.
 *
 * Rules this page keeps: every figure is one the console itself shows;
 * strategy code names never appear; the synthetic objects in the world are
 * captioned as synthetic. While the live world runs, a chapter's copy sits in
 * a fixed layer and is shown — and reachable by keyboard — only while the
 * camera holds on its layer. Without the world (reduced motion, no WebGL, a
 * phone, a failed chunk) the same chapters lay out in flow over stills
 * rendered from the real scene.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { useMarketClock } from '../lib/marketClock'
import { liveSceneWanted } from '../lib/sceneMode'
import { IconLogo } from '../components/icons'
import { MachineScene } from '../components/MachineScene'
import { Still } from '../components/Still'
import { CHAPTERS, chapterProgress, type ChapterKey } from '../scene/story'
import { RUN, SESSIONS } from '../scene/evidence'
import type { AnchorName, MachineHandle } from '../scene/machine'
import shotChain from '../shots/option-chain.webp'
import shotPulse from '../shots/market-pulse.webp'
import shotCoverage from '../shots/history-coverage.webp'
import shotFactors from '../shots/market-factors.webp'
import shotRun from '../shots/run-ledger.webp'
import shotSetup from '../shots/setup-editor.webp'
import './landing.css'

const GITHUB_URL = 'https://github.com/helloupendra/algorithmic-trading-engine'
const LICENSE_URL = `${GITHUB_URL}/blob/main/LICENSE`
const LINKEDIN_URL = 'https://www.linkedin.com/in/upendrasingh12/'
const AUTHOR = 'Upendra Singh Chauhan'

const rupees = (n: number) => `₹${Math.abs(n).toLocaleString('en-IN')}`

/* ------------------------------------------------------------------ chapters */

interface ChapterCopy {
  key: ChapterKey
  /** "01", and the layer's name as it is lettered on the page. */
  index?: string
  label: string
  title: ReactNode
  body: string
  specs?: [string, string][]
}

const COPY: ChapterCopy[] = [
  {
    key: 'hero',
    label: 'Open-source algo trading desk · Indian F&O · self-hosted',
    title: <>Not a <em>black box.</em></>,
    body: 'OpenFNO is a trading engine you can see inside: every tick stored, every rule written down, every rupee accounted for — running on your own machine, with your own broker keys.',
  },
  {
    key: 'data',
    index: '01',
    label: 'Data',
    title: <>Four feeds in. <em>Every tick kept.</em></>,
    body: 'Dhan, FYERS, Angel One and TrueData stream into one store as ticks, minute bars and option-chain snapshots — compressed after two hours, archived every night. Every picker in the console lists what is stored before you choose a range.',
    specs: [
      ['feeds', 'Dhan · FYERS · Angel One · TrueData'],
      ['history', '5 years of index candles, 1 minute to daily'],
      ['options', 'minute-level, ATM ±10 · NIFTY · BANKNIFTY · SENSEX'],
      ['store', 'TimescaleDB · NSE · BSE · MCX'],
    ],
  },
  {
    key: 'chain',
    index: '02',
    label: 'Option chain',
    title: <>Every strike. <em>Any minute.</em></>,
    body: 'Calls one side, puts the other, the strike ladder between them. Live, with Greeks, build-up, PCR, max pain and the walls — and the same chain rebuilt for any past minute from stored history, which is what makes an option strategy testable at all.',
    specs: [
      ['live', 'Greeks · long build-up / short covering · PCR · max pain'],
      ['replay', 'the chain as it stood at any stored minute'],
      ['yours', 'open positions marked on the strikes you hold'],
    ],
  },
  {
    key: 'strategy',
    index: '03',
    label: 'Strategy',
    title: <>Rules you can <em>read.</em></>,
    body: 'A strategy is a list of conditions — price against VWAP or an EMA, an EMA pair, RSI, candle body, Supertrend, ADX, the opening range, the gap. Every bar passes through them; if they all hold, there is a signal. One on_bar contract runs live and in replay.',
    specs: [
      ['catalogue', '25 strategies, described from their own code'],
      ['builder', 'write your own in the console, as conditions'],
      ['contract', 'one on_bar — the same live and in replay'],
    ],
  },
  {
    key: 'risk',
    index: '04',
    label: 'Risk',
    title: <>Three stops. <em>One switch.</em></>,
    body: 'A limit on the leg, on the group and on the day — editable while the run is going, measured per trading day — and a kill switch that survives a restart. Every stop says why it stopped: guard, limit, market close, you, or the end of the range.',
    specs: [
      ['levels', 'leg → group → day'],
      ['execution', 'paper, on real ticks · lots × lot size'],
      ['silence', 'none — a stalled feed or a dead runner says so'],
    ],
  },
  {
    key: 'ledger',
    index: '05',
    label: 'Ledger',
    title: <>The run <em>that lost.</em></>,
    body: `One tile for every session of a real run: a directional strategy on ${RUN.underlying} one-minute candles, January to September 2026. ${RUN.trades} closed trades, ${RUN.winRatePct}% winners, ${rupees(RUN.netPnl)} lost on ${rupees(RUN.initialCapital)}. It is here because a platform that only shows its wins is a pitch.`,
    specs: [
      ['net', `−${rupees(RUN.netPnl)} on ${rupees(RUN.initialCapital)}`],
      ['per trade', `−₹${Math.abs(RUN.perTrade)} · ${RUN.winRatePct}% winners`],
      ['days', `${RUN.profitableSessions} of ${RUN.sessions} positive`],
      ['drawdown', rupees(RUN.maxDrawdown)],
    ],
  },
  {
    key: 'open',
    label: 'Open source',
    title: <>Read <em>every line.</em></>,
    body: 'A .NET 10 API, a Python engine, a React console, TimescaleDB and Redis — on hardware you control. No data leaves, no strategy is uploaded, and the whole engine is on GitHub.',
  },
]

/** Labels that live at points in the world, and the chapter each belongs to. */
const TAGS: { anchor: AnchorName; chapter: ChapterKey; text: string; also?: ChapterKey; tone?: 'caption' | 'alert' }[] = [
  { anchor: 'feed0', chapter: 'data', text: 'Dhan' },
  { anchor: 'feed1', chapter: 'data', text: 'FYERS' },
  { anchor: 'feed2', chapter: 'data', text: 'Angel One' },
  { anchor: 'feed3', chapter: 'data', text: 'TrueData' },
  { anchor: 'tape', chapter: 'data', text: 'synthetic tape · not a quote', tone: 'caption' },
  { anchor: 'chain', chapter: 'chain', text: 'synthetic chain · not a quote', tone: 'caption' },
  { anchor: 'gate0', chapter: 'strategy', text: 'close > vwap' },
  { anchor: 'gate1', chapter: 'strategy', text: 'close > ema:9' },
  { anchor: 'gate2', chapter: 'strategy', text: 'body ≥ 0.5' },
  { anchor: 'gate3', chapter: 'strategy', text: 'rsi ↑ 60' },
  { anchor: 'ringLeg', chapter: 'risk', text: 'leg' },
  { anchor: 'ringGroup', chapter: 'risk', text: 'group' },
  { anchor: 'ringDay', chapter: 'risk', text: 'day' },
  { anchor: 'kill', chapter: 'risk', text: 'kill switch', tone: 'alert' },
  { anchor: 'threadEnd', chapter: 'ledger', text: `−${rupees(RUN.netPnl)}`, tone: 'alert' },
]

const SCREENS = [
  { src: shotPulse, w: 2200, h: 924, name: 'Market pulse', text: 'The first screen: indices, the nearest MCX contracts and the large caps, each with its day range and how old its last price is. A stale price says so.', alt: 'The overview screen: market state, indices, MCX commodities and large caps, each with the day range and the age of its last price.' },
  { src: shotChain, w: 2200, h: 1291, name: 'Option chain', text: 'Calls left, puts right, strike in the middle; open interest and its change, IV, and what the pair of them means. Stamped with when it was captured and by which vendor.', alt: 'The option chain: calls left, puts right, strike in the middle, with open interest, its change, IV and the build-up on each row.' },
  { src: shotCoverage, w: 2100, h: 1112, name: 'History on hand', text: 'What is actually on disk — index, resolution, first and last session, bars, and whether they came from a backfill or the live feed — before you are asked to pick a range.', alt: 'The stored history table: index, resolution, the date range, sessions, bars and where each range came from.' },
  { src: shotSetup, w: 2000, h: 924, name: 'Setup editor', text: 'A long setup and a short setup written as conditions, with the rules the run will trade under listed beneath them.', alt: 'The setup editor: a long setup and a short setup written as conditions, with the run\'s rules under them.' },
  { src: shotFactors, w: 2200, h: 1222, name: 'Market factors', text: 'Walls, max pain, PCR, ATM IV, participant open interest and flows — each stamped with how fresh it is and with what our own testing found, including "not tested yet".', alt: 'The market factors screen: option walls, max pain, put-call ratio, ATM IV and India VIX, each labelled with how fresh it is and what testing it has had.' },
  { src: shotRun, w: 2100, h: 1351, name: 'A finished run', text: 'Net P&L, win rate, profit factor, drawdown — and the account panel, because a total says nothing about whether the account survived to collect it.', alt: 'A completed backtest: net P&L, trades, win rate, profit factor, drawdown, and an account panel with the lowest balance and the closing balance.' },
]

/* -------------------------------------------------------------------- marks */

function GitHubMark() {
  return (
    <svg viewBox="0 0 16 16" width="17" height="17" fill="currentColor" aria-hidden="true">
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" />
    </svg>
  )
}

const Arrow = () => <span className="arrow" aria-hidden="true">→</span>

/** The account's equity over the run, as an SVG path drawn from the same sessions the world uses. */
function EquityCurve() {
  const { path, startY, endX, endY } = useMemo(() => {
    const W = 1000
    const H = 260
    const values = SESSIONS.map((s) => s[4])
    const min = Math.min(...values, RUN.initialCapital)
    const max = Math.max(...values, RUN.initialCapital)
    const x = (i: number) => (i / (values.length - 1)) * W
    const y = (v: number) => 14 + (1 - (v - min) / (max - min)) * (H - 28)
    return {
      path: values.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join(' '),
      startY: y(RUN.initialCapital),
      endX: W,
      endY: y(values[values.length - 1]),
    }
  }, [])
  return (
    <svg className="curve" viewBox="0 0 1000 260" role="img" aria-label={`The account's equity over the run: it starts at ${rupees(RUN.initialCapital)}, rises briefly, and ends at ${rupees(RUN.closingEquity)}.`} preserveAspectRatio="none">
      <line x1="0" x2="1000" y1={startY} y2={startY} className="curve__start" />
      <path d={path} className="curve__line" />
      <circle cx={endX - 3} cy={endY} r="5" className="curve__end" />
    </svg>
  )
}

/* ------------------------------------------------------------------ chapter */

function Chapter({ c, heading, children }: { c: ChapterCopy; heading: 'h1' | 'h2'; children?: ReactNode }) {
  const H = heading
  return (
    <div className="chapter__inner">
      {c.index && <span className="chapter__index" aria-hidden="true">{c.index}</span>}
      <p className="eyebrow"><i aria-hidden="true" />{c.index ? `Layer ${c.index} · ${c.label}` : c.label}</p>
      <H className="chapter__title" id={`${c.key}-title`}>{c.title}</H>
      <p className="chapter__body">{c.body}</p>
      {c.specs && (
        <dl className="specs">
          {c.specs.map(([k, v]) => (
            <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      )}
      {children}
    </div>
  )
}

/* --------------------------------------------------------------------- page */

export function LandingPage() {
  const { isAuthenticated, isAdmin, isLoading } = useAuth()
  // isLoading is only true while a stored token is verified against /me
  // (anonymous visitors never see it). During that probe the CTA already reads
  // as the signed-in variant and points at /login, which bounces a valid session
  // to its role home — so nothing visibly flips once the probe resolves.
  const sessionLikely = isLoading || isAuthenticated
  const consoleHref = isAuthenticated ? (isAdmin ? '/admin' : '/trader') : '/login'
  const consoleLabel = sessionLikely ? 'Go to console' : 'Open the console'

  const clock = useMarketClock()
  const [live, setLive] = useState(() => liveSceneWanted())
  const rootRef = useRef<HTMLDivElement | null>(null)
  const storyRef = useRef<HTMLDivElement | null>(null)
  const chaptersRef = useRef<HTMLDivElement | null>(null)
  const hudRef = useRef<HTMLDivElement | null>(null)
  const railRef = useRef<HTMLElement | null>(null)
  const activeRef = useRef(0)

  useEffect(() => {
    const previous = document.title
    document.title = 'OpenFNO — not a black box. An open-source algo trading desk for Indian F&O'
    return () => {
      document.title = previous
    }
  }, [])

  // The bar gains a backdrop once the page has moved; the fixed copy and labels
  // step aside once the story has been read.
  useEffect(() => {
    const onScroll = () => {
      const root = rootRef.current
      const story = storyRef.current
      if (!root) return
      root.classList.toggle('is-scrolled', window.scrollY > 24)
      if (story) root.classList.toggle('is-past', story.getBoundingClientRect().bottom <= window.innerHeight + 2)
    }
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  /** Scroll to the middle of a chapter's plateau (links and the rail; the copy itself is in a fixed layer). */
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

  /** After each rendered frame: which chapter is up, and where the world's labels go. */
  const onFrame = useCallback((m: MachineHandle) => {
    const st = m.state()
    const nearest = Math.round(st.u)
    const active = Math.abs(st.u - nearest) < 0.36 ? nearest : -1
    if (active !== activeRef.current) {
      activeRef.current = active
      const kids = chaptersRef.current?.children
      if (kids) {
        for (let i = 0; i < kids.length; i++) {
          const el = kids[i] as HTMLElement
          el.classList.toggle('is-on', i === active)
          el.inert = i !== active
        }
      }
      railRef.current?.querySelectorAll('button').forEach((b, i) => b.classList.toggle('is-on', i + 1 === active))
      railRef.current?.classList.toggle('is-on', active >= 1 && active <= 5)
    }
    const hud = hudRef.current
    if (!hud) return
    const kids = hud.children
    for (let i = 0; i < kids.length; i++) {
      const el = kids[i] as HTMLElement
      const tag = TAGS[i]
      const chapterIndex = CHAPTERS.findIndex((c) => c.key === tag.chapter)
      const alsoIndex = tag.also ? CHAPTERS.findIndex((c) => c.key === tag.also) : -2
      const near = Math.max(1 - Math.abs(st.u - chapterIndex) * 1.8, alsoIndex >= 0 ? (1 - Math.abs(st.u - alsoIndex) * 1.8) * 0.55 : 0)
      const a = m.project(tag.anchor)
      const opacity = a.visible ? Math.max(0, Math.min(1, near)) : 0
      el.style.opacity = opacity.toFixed(2)
      if (opacity > 0) el.style.transform = `translate(${a.x.toFixed(1)}px, ${a.y.toFixed(1)}px)`
    }
  }, [])

  return (
    <div className={`lp ${live ? 'lp--live' : 'lp--still'}`} ref={rootRef}>
      <header className="top">
        <a className="brand" href="#top" aria-label="OpenFNO home">
          <span className="brand__mark"><IconLogo /></span>
          <span className="brand__word">openfno</span>
        </a>
        <nav className="top__links" aria-label="Sections">
          <button type="button" onClick={() => goTo('data')}>The engine</button>
          <a href="#console">Console</a>
          <a href="#proof">Proof</a>
          <a href="/docs/">Docs</a>
        </nav>
        <div className="top__end">
          <span className={`market${clock.open ? ' is-open' : ''}`} title="NSE cash timings, holidays aside">
            <i aria-hidden="true" />NSE {clock.open ? 'open' : 'closed'} · {clock.time} IST
          </span>
          <a className="top__icon" href={GITHUB_URL} target="_blank" rel="noopener noreferrer" aria-label="Source on GitHub"><GitHubMark /></a>
          <Link className="cta cta--solid cta--sm" to={consoleHref}>{sessionLikely ? 'Console' : 'Open console'}</Link>
        </div>
      </header>

      <main id="top">
        <div className="story" ref={storyRef}>
          {live && (
            <>
              <MachineScene mode="story" trackRef={storyRef} onFrame={onFrame} onUnavailable={() => setLive(false)} />

              {/* Labels that live at points in the world. */}
              <div className="hud" ref={hudRef} aria-hidden="true">
                {TAGS.map((t) => (
                  <span key={t.anchor} className={`tag${t.tone ? ` tag--${t.tone}` : ''}`}>{t.text}</span>
                ))}
              </div>

              {/* One chapter of copy at a time, over the held world. */}
              <div className="chapters" ref={chaptersRef}>
                {COPY.map((c, i) => (
                  <section
                    key={c.key}
                    className={`chapter chapter--${c.key}${i === 0 ? ' is-on' : ''}${i > 0 && i < 6 ? (i % 2 ? ' chapter--left' : ' chapter--right') : ''}`}
                    aria-labelledby={`${c.key}-title`}
                    inert={i !== 0}
                  >
                    <Chapter c={c} heading={i === 0 ? 'h1' : 'h2'}>
                      {i === 0 && (
                        <div className="actions">
                          <Link className="cta cta--solid" to={consoleHref}>{consoleLabel} <Arrow /></Link>
                          <a className="cta cta--line" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Read the source</a>
                        </div>
                      )}
                      {c.key === 'open' && (
                        <div className="actions">
                          <a className="cta cta--solid" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Read the source</a>
                          <Link className="cta cta--line" to={consoleHref}>{consoleLabel} <Arrow /></Link>
                        </div>
                      )}
                    </Chapter>
                  </section>
                ))}
              </div>

              <nav className="rail" ref={railRef} aria-label="Layers of the engine">
                {COPY.slice(1, 6).map((c) => (
                  <button key={c.key} type="button" onClick={() => goTo(c.key)}><span>{c.label}</span><i aria-hidden="true" /></button>
                ))}
              </nav>

              <p className="cue" aria-hidden="true"><span />scroll to take the lid off</p>
              <div className="story__length" aria-hidden="true" />
            </>
          )}

          {!live && (
            <div className="stills">
              {COPY.map((c, i) => (
                <section key={c.key} className={`still-chapter still-chapter--${c.key}`} id={c.key} aria-labelledby={`${c.key}-title`}>
                  <Still pose={c.key} className="still-chapter__bg" />
                  <Chapter c={c} heading={i === 0 ? 'h1' : 'h2'}>
                    {(i === 0 || c.key === 'open') && (
                      <div className="actions">
                        <Link className="cta cta--solid" to={consoleHref}>{consoleLabel} <Arrow /></Link>
                        <a className="cta cta--line" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Read the source</a>
                      </div>
                    )}
                  </Chapter>
                </section>
              ))}
            </div>
          )}
        </div>

        {/* ------------------------------------------------------- console */}
        <section id="console" className="band" aria-labelledby="console-title">
          <div className="band__head">
            <p className="eyebrow"><i aria-hidden="true" />The console</p>
            <h2 id="console-title">Six screens. <em>No mock-ups.</em></h2>
            <p>Captures of this deployment, as it ran. Scroll sideways.</p>
          </div>
          <div className="reel" tabIndex={0} role="group" aria-label="Console screens">
            {SCREENS.map((s, i) => (
              <figure className="reel__card" key={s.name}>
                <div className="reel__frame">
                  <img src={s.src} width={s.w} height={s.h} alt={s.alt} loading="lazy" decoding="async" />
                </div>
                <figcaption>
                  <span className="reel__n">{String(i + 1).padStart(2, '0')}</span>
                  <b>{s.name}</b>
                  <span>{s.text}</span>
                </figcaption>
              </figure>
            ))}
          </div>
        </section>

        {/* --------------------------------------------------------- proof */}
        <section id="proof" className="band band--proof" aria-labelledby="proof-title">
          <div className="band__head">
            <p className="eyebrow"><i aria-hidden="true" />Proof, not promises</p>
            <h2 id="proof-title">The only run we show in full <em>lost money.</em></h2>
            <p>
              A directional strategy replayed over {RUN.underlying} one-minute candles, {RUN.sessions} sessions from January to
              September 2026, every option leg priced from stored premiums with brokerage and slippage applied. Entries that
              could not be priced are listed as skipped, never filled at a made-up price.
            </p>
          </div>
          <div className="proof">
            <div className="proof__figure">
              <b>−{rupees(RUN.netPnl)}</b>
              <span>net, on a {rupees(RUN.initialCapital)} account</span>
            </div>
            <EquityCurve />
            <dl className="proof__row">
              <div><dt>closed trades</dt><dd>{RUN.trades}</dd></div>
              <div><dt>winners</dt><dd>{RUN.winRatePct}%</dd></div>
              <div><dt>a trade</dt><dd>−₹{Math.abs(RUN.perTrade)}</dd></div>
              <div><dt>days positive</dt><dd>{RUN.profitableSessions} / {RUN.sessions}</dd></div>
              <div><dt>deepest fall</dt><dd>{rupees(RUN.maxDrawdown)}</dd></div>
              <div><dt>lowest balance</dt><dd>{rupees(RUN.lowestEquity)}</dd></div>
            </dl>
          </div>
        </section>

        {/* -------------------------------------------------------- run it */}
        <section id="run" className="band band--run" aria-labelledby="run-title">
          <div className="band__head">
            <p className="eyebrow"><i aria-hidden="true" />Yours</p>
            <h2 id="run-title">Your machine. <em>Your keys.</em></h2>
            <p>The whole desk runs on hardware you control. No data leaves, and no strategy is uploaded anywhere.</p>
            <div className="actions">
              <a className="cta cta--solid" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Read the source</a>
              <a className="cta cta--line" href="/docs/">Read the docs <Arrow /></a>
            </div>
          </div>
          <div className="term" role="group" aria-label="Quick start">
            <div className="term__bar" aria-hidden="true"><i /><i /><i /><span>quick start</span></div>
            <pre><code>
              <span className="c"># 1 · clone and bootstrap — databases, keys, builds</span>{'\n'}
              <span className="p">$</span> git clone {GITHUB_URL}.git{'\n'}
              <span className="p">$</span> cd algorithmic-trading-engine{'\n'}
              <span className="p">$</span> ./scripts/setup.sh{'\n\n'}
              <span className="c"># 2 · start the API — it applies its own migrations</span>{'\n'}
              <span className="p">$</span> dotnet run --project src/AlgoTrading.Api{'\n\n'}
              <span className="c"># 3 · load expiries and the instrument masters</span>{'\n'}
              <span className="p">$</span> ./scripts/load-data.sh
            </code></pre>
            <p className="term__stack">.NET 10 API · Python engine · React 19 console · TimescaleDB · Redis · SignalR</p>
          </div>
        </section>
      </main>

      <footer className="foot">
        <p className="foot__word" aria-hidden="true">openfno</p>
        <div className="foot__row">
          <span>Built by {AUTHOR}</span>
          <span className="foot__links">
            <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer">GitHub</a>
            <a href={LINKEDIN_URL} target="_blank" rel="noopener noreferrer">LinkedIn</a>
            <a href="/docs/">Docs</a>
            <a href={LICENSE_URL} target="_blank" rel="noopener noreferrer">Licence</a>
            <Link to={consoleHref}>Console</Link>
          </span>
        </div>
        <p className="foot__risk">Trading involves financial risk. OpenFNO executes on paper against live ticks; validate every strategy before you trust it with money.</p>
      </footer>
    </div>
  )
}
