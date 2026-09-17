/**
 * Public homepage ("/").
 *
 * The composition is one idea: light, then the product, then the evidence. A
 * field of colour (components/AuroraScene) lights the top of the page; the
 * console itself stands in it, in a frame tilted back like a screen on a desk
 * and mirrored in the floor under it; everything below is flat, quiet and
 * factual so the screens stay the loudest thing here.
 *
 * Every figure is one the console itself shows: the catalogue's 25 strategies,
 * the stored ranges on the backtesting page, the vendors on the broker page.
 * The screens under /shots are captures of this deployment with the strategy
 * code names cropped out — public pages name what a strategy does, never what it
 * is called. The only run shown in full is one that lost money: a homepage that
 * promises returns has stopped being evidence.
 */

import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
  type RefObject,
} from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { AuroraCanvas } from '../components/AuroraScene'
import { prefersReducedMotion } from '../lib/motion'
import './landing.css'

/* ------------------------------------------------------- small behaviours */

function useReveal(rootRef: RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const root = rootRef.current
    if (!root) return
    const els = Array.from(root.querySelectorAll<HTMLElement>('.reveal'))
    if (!('IntersectionObserver' in window) || prefersReducedMotion()) {
      els.forEach((el) => el.classList.add('is-in'))
      return
    }
    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((e) => {
          if (e.isIntersecting) {
            e.target.classList.add('is-in')
            io.unobserve(e.target)
          }
        })
      },
      { rootMargin: '0px 0px -6% 0px', threshold: 0.06 },
    )
    els.forEach((el) => io.observe(el))
    return () => io.disconnect()
  }, [rootRef])
}

/**
 * Pointer-driven 3D tilt; rAF-throttled, cleared on leave. It writes --rx/--ry
 * rather than the transform, so the element keeps the resting angle its
 * stylesheet gives it and the pointer only bends it from there.
 */
function useTilt(strength = 1) {
  const raf = useRef(0)
  const enabled = useRef(false)
  useEffect(() => {
    enabled.current = !prefersReducedMotion() && window.matchMedia('(hover: hover)').matches
  }, [])

  const onPointerMove = (ev: ReactPointerEvent<HTMLElement>) => {
    if (!enabled.current) return
    const card = ev.currentTarget
    const r = card.getBoundingClientRect()
    const px = (ev.clientX - r.left) / r.width
    const py = (ev.clientY - r.top) / r.height
    if (raf.current) cancelAnimationFrame(raf.current)
    raf.current = requestAnimationFrame(() => {
      raf.current = 0
      card.style.setProperty('--rx', `${((0.5 - py) * 8 * strength).toFixed(2)}deg`)
      card.style.setProperty('--ry', `${((px - 0.5) * 10 * strength).toFixed(2)}deg`)
      card.style.setProperty('--mx', `${(px * 100).toFixed(1)}%`)
      card.style.setProperty('--my', `${(py * 100).toFixed(1)}%`)
    })
  }
  const onPointerLeave = (ev: ReactPointerEvent<HTMLElement>) => {
    if (raf.current) {
      cancelAnimationFrame(raf.current)
      raf.current = 0
    }
    ev.currentTarget.style.removeProperty('--rx')
    ev.currentTarget.style.removeProperty('--ry')
  }
  return { onPointerMove, onPointerLeave }
}

/** A glass tile that lights under the pointer. `span` is its width in grid columns. */
function Tile({
  children,
  span = 2,
  delay,
}: {
  children: ReactNode
  span?: number
  delay?: number
}) {
  const tilt = useTilt(0.6)
  const style = { '--span': span } as CSSProperties
  if (delay) (style as Record<string, string>)['--d'] = `${delay}s`
  return (
    <article
      className="tile reveal"
      style={style}
      onPointerMove={tilt.onPointerMove}
      onPointerLeave={tilt.onPointerLeave}
    >
      {children}
    </article>
  )
}

const Mark = () => (
  <svg viewBox="0 0 24 24" aria-hidden="true">
    <g fill="currentColor" stroke="none"><rect x="5.25" y="6.5" width="4.5" height="2.3" rx="1.15"/><rect x="14.25" y="6.5" width="4.5" height="2.3" rx="1.15"/><rect x="3" y="10.7" width="6.75" height="2.6" rx="1.3"/><rect x="14.25" y="10.7" width="6.75" height="2.6" rx="1.3"/><rect x="5.25" y="15.2" width="4.5" height="2.3" rx="1.15"/><rect x="14.25" y="15.2" width="4.5" height="2.3" rx="1.15"/><rect x="11.5" y="4.8" width="1" height="14.4" rx=".5" opacity=".9"/></g>
  </svg>
)

/* ------------------------------------------------------------ the console */

/** Screens from this deployment. The caption says what each one is for. */
const SCREENS = [
  {
    key: 'chain',
    tab: 'Option chain',
    src: '/shots/option-chain.webp',
    w: 1600,
    h: 939,
    alt: 'The option chain: calls left, puts right, strike in the middle, with open interest, its change, IV and the build-up on each row.',
    title: 'The chain, read the way a trader reads it',
    text: 'Calls left, puts right, strike in the middle, the spot line between the two strikes the price sits between. Open interest and its change, IV, volume — and what the pair of them means: long build-up, short covering. PCR, max pain and the walls come from the same snapshot, stamped with when it was captured and by which vendor.',
  },
  {
    key: 'pulse',
    tab: 'Market pulse',
    src: '/shots/market-pulse.webp',
    w: 1600,
    h: 669,
    alt: 'The overview screen: market state, indices, MCX commodities and large caps, each with the day range and the age of its last price.',
    title: 'First screen: what the market is doing',
    text: 'Indices, the nearest MCX contracts and the large caps that carry the index weight — each with its day range and, in plain words, how old the last price is. A stale price says "5h ago" instead of pretending to be live, and the market card knows the holiday calendar, so "closed" arrives with the next open.',
  },
  {
    key: 'history',
    tab: 'History on hand',
    src: '/shots/history-coverage.webp',
    w: 1500,
    h: 798,
    alt: 'The stored history table: index, resolution, the date range, sessions, bars and where each range came from.',
    title: 'What is stored, before you pick anything',
    text: 'Every picker is built from an inventory of what is actually on disk: index, resolution, first and last session, how many bars, and whether they arrived from a backfill or from the live feed. You cannot ask this platform to replay a range it does not have — it shows you the ranges first.',
  },
  {
    key: 'factors',
    tab: 'Market factors',
    src: '/shots/market-factors.webp',
    w: 1600,
    h: 885,
    alt: 'The market factors screen: option walls, max pain, put-call ratio, ATM IV and India VIX, each labelled with how fresh it is and what testing it has had.',
    title: 'What desks read — and how much of it holds',
    text: 'Option walls, max pain, the put-call ratio, ATM IV, participant open interest and the FII/DII flows, each stamped with how fresh it is. Every section also carries what our own testing found: the levels here are marked "not tested yet", and the PCR and OI-change signals are marked as having made no money after costs on 2021–2025 NIFTY data.',
  },
  {
    key: 'run',
    tab: 'A finished run',
    src: '/shots/run-ledger.webp',
    w: 1500,
    h: 954,
    alt: 'A completed backtest: net P&L, trades, win rate, profit factor, drawdown, and an account panel with the lowest balance and the closing balance.',
    title: 'A real run, including the part that hurts',
    text: 'A directional strategy replayed over BANKNIFTY one-minute candles from January to September 2026: 760 closed trades, 35% winners, ₹1,05,391 lost on a ₹10,00,000 account. The account panel is there because a total says nothing about whether the account survived to collect it.',
  },
] as const

function Console() {
  const [active, setActive] = useState(0)
  const tilt = useTilt(0.5)
  const screen = SCREENS[active]

  return (
    <div className="showcase">
      <div className="showcase__tabs" role="tablist" aria-label="Console screens">
        {SCREENS.map((s, i) => (
          <button
            key={s.key}
            role="tab"
            type="button"
            aria-selected={i === active}
            className={`tab${i === active ? ' is-on' : ''}`}
            onClick={() => setActive(i)}
          >
            {s.tab}
          </button>
        ))}
      </div>

      <div className="showcase__stage">
        <figure className="screen" onPointerMove={tilt.onPointerMove} onPointerLeave={tilt.onPointerLeave}>
          <div className="screen__bar" aria-hidden="true">
            <i /><i /><i />
            <span>openfno.com/admin</span>
          </div>
          <div className="screen__shot">
            {SCREENS.map((s, i) => (
              <img
                key={s.key}
                src={s.src}
                width={s.w}
                height={s.h}
                alt={i === active ? s.alt : ''}
                aria-hidden={i === active ? undefined : true}
                className={i === active ? 'is-on' : ''}
                loading={i === 0 ? 'eager' : 'lazy'}
                decoding="async"
              />
            ))}
          </div>
        </figure>
        {/* The floor: the same picture, upside down and faded into the page. */}
        <div className="screen__floor" aria-hidden="true">
          <img src={screen.src} alt="" width={screen.w} height={screen.h} loading="lazy" decoding="async" />
        </div>
      </div>

      <div className="showcase__say">
        <h3>{screen.title}</h3>
        <p>{screen.text}</p>
      </div>
    </div>
  )
}

/* ---------------------------------------------------------------- page */

/**
 * Where the code and its author live. Three placements, one definition: the nav
 * mark (the convention for an open-source product page), the final call to
 * action, and the footer attribution.
 */
const GITHUB_URL = 'https://github.com/helloupendra/algorithmic-trading-engine'
const LINKEDIN_URL = 'https://www.linkedin.com/in/upendrasingh12/'
const AUTHOR = 'Upendra Singh Chauhan'

/** One example setup, as the console would hold it: two sides, four conditions each. */
const SETUP = [
  { side: 'long', conditions: ['close>vwap', 'close>ema:9', 'body>=0.5', 'rsi_cross_up:60'] },
  { side: 'short', conditions: ['close<vwap', 'close<ema:9', 'body>=0.5', 'rsi_cross_down:40'] },
] as const

/** What the store carries, as a quiet rail under the hero. */
const TAPE = [
  'NIFTY 50', 'BANK NIFTY', 'SENSEX', 'FINNIFTY', 'MIDCPNIFTY', 'BANKEX',
  'INDIA VIX', 'CRUDE OIL', 'NATURAL GAS', 'GOLD', 'SILVER', 'NIFTY 50 STOCKS',
]

function GitHubMark() {
  return (
    <svg viewBox="0 0 16 16" width="18" height="18" fill="currentColor" aria-hidden="true">
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" />
    </svg>
  )
}

function LinkedInMark() {
  return (
    <svg viewBox="0 0 16 16" width="16" height="16" fill="currentColor" aria-hidden="true">
      <path d="M13.63 13.63h-2.37V9.92c0-.89-.02-2.02-1.23-2.02-1.23 0-1.42.96-1.42 1.96v3.77H6.24V6h2.28v1.04h.03c.32-.6 1.09-1.23 2.25-1.23 2.4 0 2.85 1.58 2.85 3.64v4.18zM3.56 4.96a1.37 1.37 0 1 1 0-2.75 1.37 1.37 0 0 1 0 2.75zM4.75 13.63H2.37V6h2.38v7.63zM14.82 0H1.18C.53 0 0 .52 0 1.15v13.7C0 15.48.53 16 1.18 16h13.64c.65 0 1.18-.52 1.18-1.15V1.15C16 .52 15.47 0 14.82 0z" />
    </svg>
  )
}

function Icon({ path }: { path: ReactNode }) {
  return (
    <span className="tile__icon" aria-hidden="true">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
        {path}
      </svg>
    </span>
  )
}

export function LandingPage() {
  const { isAuthenticated, isAdmin, isLoading } = useAuth()
  // isLoading is only true while a stored token is verified against /me
  // (anonymous visitors never see it). During that probe the CTA already reads
  // as the signed-in variant and points at /login, which bounces a valid session
  // to its role home — so nothing visibly flips once the probe resolves.
  const sessionLikely = isLoading || isAuthenticated
  const consoleHref = isAuthenticated ? (isAdmin ? '/admin' : '/trader') : '/login'
  const consoleLabel = sessionLikely ? 'Go to console' : 'Open the console'
  const navLabel = sessionLikely ? 'Console' : 'Open console'

  const rootRef = useRef<HTMLDivElement | null>(null)
  useReveal(rootRef)

  useEffect(() => {
    const previous = document.title
    document.title = 'OpenFNO — run it live, replay five years, see every rupee'
    return () => {
      document.title = previous
    }
  }, [])

  return (
    <div className="lp" ref={rootRef}>
      <AuroraCanvas className="lp__light" />

      <header className="nav">
        <div className="nav__inner">
          <a className="brand" href="#top" aria-label="OpenFNO home">
            <span className="brand__mark"><Mark /></span>
            <span className="brand__word">open<b>fno</b></span>
          </a>
          <nav className="nav__links" aria-label="Sections">
            <a href="#console">Console</a>
            <a href="#modules">Modules</a>
            <a href="#setup">Build a setup</a>
            <a href="#evidence">Evidence</a>
            <a href="/docs/">Docs</a>
          </nav>
          <div className="nav__end">
            <a className="nav__icon" href={GITHUB_URL} target="_blank" rel="noopener noreferrer" aria-label="Source on GitHub" title="Source on GitHub">
              <GitHubMark />
            </a>
            <Link className="btn btn--primary btn--sm" to={consoleHref}>{navLabel}</Link>
          </div>
        </div>
      </header>

      <main id="top">
        {/* ------------------------------------------------------- hero */}
        <section className="hero" aria-labelledby="hero-title">
          <div className="wrap hero__copy">
            <span className="eyebrow"><span className="dot" aria-hidden="true" />Open-source F&amp;O desk · self-hosted · your broker keys</span>
            <h1 id="hero-title">
              Run it live.<br />
              Replay five years.<br />
              <em>See every rupee.</em>
            </h1>
            <p className="lead">
              One desk for Indian F&amp;O — four vendor feeds into a single store, 25 strategies plus one
              you write in the console, and a replay engine that prices every trade from stored option
              premiums with your own windows, exits, limits and costs applied.
            </p>
            <div className="cta">
              <Link className="btn btn--primary btn--lg" to={consoleHref}>{consoleLabel}</Link>
              <a className="btn btn--glass btn--lg" href="#console">See the console</a>
            </div>
            <p className="hero__vendors">
              Dhan · FYERS · Angel One · TrueData&nbsp;&nbsp;—&nbsp;&nbsp;NSE · BSE · MCX
            </p>
          </div>

          <div className="wrap wrap--wide" id="console">
            <Console />
          </div>
        </section>

        {/* the instruments the store carries */}
        <div className="tape" aria-hidden="true">
          <div className="tape__run">
            {[0, 1].map((copy) => (
              <span className="tape__set" key={copy}>
                {TAPE.map((t) => (
                  <span className="tape__item" key={t}>{t}</span>
                ))}
              </span>
            ))}
          </div>
        </div>

        {/* ----------------------------------------------------- numbers */}
        <section className="band" aria-label="Platform facts">
          <div className="wrap band__grid">
            <div className="fig reveal"><b>25</b><span>strategies in the catalogue</span></div>
            <div className="fig reveal" style={{ '--d': '0.06s' } as CSSProperties}><b>5 yrs</b><span>of index candles, 1m to daily</span></div>
            <div className="fig reveal" style={{ '--d': '0.12s' } as CSSProperties}><b>±10</b><span>strikes of stored option history</span></div>
            <div className="fig reveal" style={{ '--d': '0.18s' } as CSSProperties}><b>3 s</b><span>risk guard interval, live</span></div>
          </div>
        </section>

        {/* ----------------------------------------------------- modules */}
        <section id="modules" aria-labelledby="modules-title">
          <div className="wrap">
            <div className="head reveal">
              <p className="kicker">Six modules, one pipeline</p>
              <h2 id="modules-title">Data in. Decisions out.<br />Nothing in between hidden.</h2>
            </div>

            <div className="bento">
              <Tile span={3}>
                <Icon path={<><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" /><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></>} />
                <h3>Feeds, history, archive</h3>
                <p>Four vendors — Dhan, FYERS, Angel One, TrueData — into one TimescaleDB store. Ticks, minute bars and chain snapshots, compressed at two hours and archived every night.</p>
                <ul>
                  <li>Backfill bounded by each vendor's real limits</li>
                  <li>An honest heartbeat: a stalled feed says so</li>
                </ul>
              </Tile>

              <Tile span={3} delay={0.06}>
                <Icon path={<><path d="M12 3 3 7.5 12 12l9-4.5L12 3Z" /><path d="m3 12 9 4.5L21 12" /><path d="m3 16.5 9 4.5 9-4.5" /></>} />
                <h3>Option chain &amp; open interest</h3>
                <p>The live chain with Greeks, build-up, PCR and max pain — and the same chain rebuilt for any past minute from stored history.</p>
                <ul>
                  <li>Minute-level chain history, ATM ±10 strikes</li>
                  <li>Your positions marked on the strikes you hold</li>
                </ul>
              </Tile>

              <Tile span={2} delay={0.04}>
                <Icon path={<><rect x="4" y="8" width="16" height="12" rx="3" /><path d="M12 8V4M8 4h8M9 14h.01M15 14h.01" /></>} />
                <h3>Catalogue &amp; builder</h3>
                <p>25 strategies whose descriptions and data needs come from the code itself — or write your own from the console, in conditions.</p>
              </Tile>

              <Tile span={2} delay={0.1}>
                <Icon path={<><path d="M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.8 3h10.4a2 2 0 0 0 1.8-3l-5-9V3" /><path d="M7.5 15h9" /></>} />
                <h3>Replay, with your rules</h3>
                <p>The same <span className="mono">on_bar</span> contract as live. Windows, one trade a day, exits, strike offset, brokerage and slippage. Index options, or the stock itself.</p>
              </Tile>

              <Tile span={2} delay={0.16}>
                <Icon path={<><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6l8-3z" /><path d="M9 12l2 2 4-4" /></>} />
                <h3>Risk, three levels</h3>
                <p>A stop on the leg, a stop on the group, a stop on the day — editable while the run is going, measured per trading day, with a kill switch that survives a restart.</p>
              </Tile>
            </div>
          </div>
        </section>

        {/* ------------------------------------------------ build a setup */}
        <section id="setup" aria-labelledby="setup-title">
          <div className="wrap">
            <div className="head reveal">
              <p className="kicker">Write the setup, not the plumbing</p>
              <h2 id="setup-title">A strategy you can type.</h2>
              <p>List what makes a long and what makes a short. The engine turns them into signals; the run's rules decide everything after that.</p>
            </div>

            <div className="split">
              <div className="card reveal">
                <div className="card__head"><span className="card__dot" aria-hidden="true" />The setup</div>
                <div className="setup-rows">
                  {SETUP.map((row) => (
                    <div className="setup-row" key={row.side}>
                      <span className={`side side--${row.side}`}>{row.side}</span>
                      <span className="side__conds">
                        {row.conditions.map((c) => (
                          <code key={c}>{c}</code>
                        ))}
                      </span>
                    </div>
                  ))}
                </div>
                <p className="card__note">
                  Also available: EMA pairs, Supertrend, ADX, opening-range breaks, the gap, the move from
                  the open, a volume z-score. A condition that cannot be computed yet counts as false — a
                  setup that cannot be checked has not happened.
                </p>
              </div>

              <div className="card reveal" style={{ '--d': '0.08s' } as CSSProperties}>
                <div className="card__head">And the run's own rules</div>
                <ul className="rules-list">
                  <li><b>09:20–11:00, 13:00–15:15</b> <span>two trading windows</span></li>
                  <li><b>1 trade a day</b> <span>with a 15-minute cooldown</span></li>
                  <li><b>Stop after 2 losses</b> <span>and the day is closed</span></li>
                  <li><b>−12% leg stop</b> <span>moving to entry after +15%</span></li>
                  <li><b>Step trail</b> <span>10% behind the best premium</span></li>
                  <li><b>Time exit</b> <span>45 minutes after entry</span></li>
                  <li><b>Strike offset</b> <span>one step out of the money</span></li>
                  <li><b>₹20 a leg + 0.05% slippage</b> <span>on every fill</span></li>
                </ul>
                <p className="card__note">
                  The same rules gate a live run and a replay, so the setup you tested is the setup you run.
                </p>
              </div>
            </div>
          </div>
        </section>

        {/* ---------------------------------------------------- evidence */}
        <section id="evidence" aria-labelledby="evidence-title">
          <div className="wrap">
            <div className="head reveal">
              <p className="kicker">Evidence, not promises</p>
              <h2 id="evidence-title">The run below lost ₹1,05,391.</h2>
              <p>
                It is on the homepage because it is real: a directional strategy on BANKNIFTY one-minute
                candles, January to September 2026, one lot, replayed with the leg stop and target it was
                given.
              </p>
            </div>

            <div className="split split--wide">
              <figure className="shot reveal">
                <img
                  src="/shots/run-ledger.webp"
                  width={1500}
                  height={954}
                  loading="lazy"
                  decoding="async"
                  alt="The run's numbers: net P&L minus ₹1,05,391, 760 trades, 35% win rate, 0.86 profit factor, and the account panel showing the lowest balance and the deepest fall from a peak."
                />
              </figure>

              <div className="card reveal" style={{ '--d': '0.08s' } as CSSProperties}>
                <div className="card__head">What the ledger says</div>
                <ul className="notes">
                  <li><b>760 closed trades</b>, 264 winners against 494 losers — a 35% win rate.</li>
                  <li><b>Expectancy −₹139 a trade.</b> The average win is larger than the average loss and it still loses; the count decides it.</li>
                  <li><b>75 of 176 sessions</b> ended positive. The account panel is what says the run was survivable at all.</li>
                  <li><b>Every exit carries its reason</b> — leg stop, target, a limit closing the day, or the 15:15 square-off.</li>
                  <li><b>Entries that could not be priced are listed</b>, never quietly filled at a made-up price.</li>
                </ul>
                <p className="card__note">
                  This is what the platform is for: finding that out in an evening, on stored data, before a
                  rupee is at risk.
                </p>
              </div>
            </div>
          </div>
        </section>

        {/* ----------------------------------------------- how it works */}
        <section id="how" aria-labelledby="how-title">
          <div className="wrap">
            <div className="head reveal">
              <p className="kicker">A run, end to end</p>
              <h2 id="how-title">Six steps from idea to ledger.</h2>
              <p>The same path whether you replay history or run on today's ticks.</p>
            </div>
            <ol className="steps reveal">
              <li><h4>Pick a strategy</h4><p>From the catalogue, or write the conditions yourself.</p></li>
              <li><h4>Choose what it trades</h4><p>An index and its chain, or a stock in shares. Lot size and strike step come from the instrument master.</p></li>
              <li><h4>Set the rules</h4><p>Windows, limits, exits, strike offset, costs — and the three levels of risk.</p></li>
              <li><h4>Start</h4><p>A dedicated runner process per run; its output streams to the console.</p></li>
              <li><h4>Watch positions</h4><p>Lots × lot size, entry, LTP and P&amp;L per contract; activity in plain words.</p></li>
              <li><h4>Stop with a reason</h4><p>Guard, limit, market close, you, or the end of the range — recorded, never silent.</p></li>
            </ol>
          </div>
        </section>

        {/* ------------------------------------------------- principles */}
        <section id="principles" aria-labelledby="pr-title">
          <div className="wrap">
            <div className="head reveal">
              <p className="kicker">Principles</p>
              <h2 id="pr-title">Built for operators who want to know.</h2>
            </div>
            <div className="principles">
              <div className="principle reveal"><span className="k">01</span><h3>Nothing fails silently</h3><p>A stalled feed, an expired token, a skipped entry, a runner that died — each is visible where you are looking, with a reason.</p></div>
              <div className="principle reveal" style={{ '--d': '0.06s' } as CSSProperties}><span className="k">02</span><h3>Coverage first</h3><p>Every picker is built from an inventory of what is stored. You cannot ask for a range that has no sessions.</p></div>
              <div className="principle reveal" style={{ '--d': '0.12s' } as CSSProperties}><span className="k">03</span><h3>One contract, live and replay</h3><p>Strategies implement one <span className="mono">on_bar</span>. The live runner and the backtester feed it the same shapes.</p></div>
              <div className="principle reveal" style={{ '--d': '0.18s' } as CSSProperties}><span className="k">04</span><h3>Self-hosted, your keys</h3><p>Your machine or your server, your broker credentials. Postgres/TimescaleDB, Redis, a .NET API and a Python engine — nothing leaves.</p></div>
            </div>
          </div>
        </section>

        <div className="wrap">
          <div className="stack reveal">
            <span><b>.NET 10</b> API</span><span><b>Python</b> engine</span><span><b>React 19</b> console</span><span><b>TimescaleDB</b></span><span><b>Redis</b></span><span><b>SignalR</b> live updates</span>
          </div>
        </div>

        <section className="final" aria-labelledby="final-title">
          <div className="wrap reveal">
            <h2 id="final-title">Open the console.</h2>
            <p>Sign in, start the feed, run your first strategy on paper today — and replay it over five years of stored candles tonight.</p>
            <div className="cta cta--center">
              <Link className="btn btn--primary btn--lg" to={consoleHref}>{consoleLabel}</Link>
              <a className="btn btn--glass btn--lg" href="/docs/">Read the docs</a>
              <a className="btn btn--ghost btn--lg" href={GITHUB_URL} target="_blank" rel="noopener noreferrer"><GitHubMark /> Source</a>
            </div>
          </div>
        </section>
      </main>

      <footer>
        <div className="wrap">
          <span>OpenFNO · open source · paper execution on live ticks</span>
          <span className="footer__author">
            Built by {AUTHOR}
            <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer" aria-label="GitHub"><GitHubMark /> GitHub</a>
            <a href={LINKEDIN_URL} target="_blank" rel="noopener noreferrer" aria-label="LinkedIn"><LinkedInMark /> LinkedIn</a>
            <a href="/docs/">Docs</a>
            <a href="#top">Back to top ↑</a>
          </span>
        </div>
      </footer>
    </div>
  )
}
