/**
 * Public landing page.
 *
 * Design language: modern dark SaaS — aurora glow field behind a huge gradient
 * headline, a product mock in a tilted glass browser frame, a bento feature
 * grid, and one strong CTA. All decoration is CSS; no WebGL, no images, so it
 * renders instantly everywhere and respects prefers-reduced-motion.
 */

import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'

const TICKER = [
  'NIFTYBANK 57,588.80 +0.17%',
  'NIFTY50 24,335.90 −0.12%',
  'BANKNIFTY 57600CE 538.30 +7.41%',
  'SBIN 1,063.00 −0.44%',
  'BANKNIFTY 57600PE 330.20 −17.18%',
  'HDFCBANK 720.30 +1.31%',
  'AXISBANK 1,265.00 +0.72%',
]

const BENTO = [
  {
    span: 'wide',
    kicker: 'Execution pipeline',
    title: 'Signal → risk gate → fill, recorded end to end',
    body: 'Every strategy signal passes a persistent risk gate before the paper engine fills it. Orders, positions, equity snapshots and performance metrics are written per run — nothing is a black box.',
    art: 'curve',
  },
  {
    span: '',
    kicker: 'Risk',
    title: 'A kill switch that survives restarts',
    body: 'One persisted switch halts all trading and flattens open positions. Rate caps and daily-loss limits sit in front of every order.',
    art: 'switch',
  },
  {
    span: '',
    kicker: 'Market data',
    title: 'Every tick, kept',
    body: 'FYERS WebSocket → Redis Streams → TimescaleDB hypertable. 50k+ ticks per session with raw payloads for replay.',
    art: 'ticks',
  },
  {
    span: '',
    kicker: 'Derivatives',
    title: 'Options as a first-class citizen',
    body: 'Expiry rule engine, ATM resolution and a full option-chain view over a 1,00,000-instrument master.',
    art: 'chain',
  },
  {
    span: '',
    kicker: 'Intelligence',
    title: 'News and movers, in context',
    body: 'India, global and commodity headlines plus category-wise day movers — beside your positions, not in another tab.',
    art: 'news',
  },
  {
    span: 'wide',
    kicker: 'Strategies',
    title: 'Your logic stays yours',
    body: 'Strategies plug into one Python contract and run as isolated processes. Proprietary code never enters the repository — the platform only sees signals.',
    art: 'code',
  },
]

const STEPS = [
  { n: '01', title: 'Connect & ingest', body: 'Authenticate the broker, pick a watchlist, and let the engine record ticks, bars and quotes.' },
  { n: '02', title: 'Run on paper', body: 'Deploy a strategy against live data with virtual capital. Watch fills, positions and the equity curve build in real time.' },
  { n: '03', title: 'Judge with numbers', body: 'Win rate, drawdown, profit factor, expectancy — per run, from recorded data. Only then think about going live.' },
]

function HeroMock() {
  return (
    <div className="lp-frame" aria-hidden="true">
      <div className="lp-frame__bar">
        <span /><span /><span />
        <div className="lp-frame__url">algotrading.local/trader</div>
      </div>
      <div className="lp-frame__body">
        <div className="lp-frame__side">
          <div className="lp-frame__logo">▲</div>
          {['Overview', 'Watchlist', 'News', 'Movers', 'Positions', 'Strategies'].map((item, i) => (
            <div key={item} className={`lp-frame__nav ${i === 0 ? 'is-active' : ''}`}>{item}</div>
          ))}
        </div>
        <div className="lp-frame__main">
          <div className="lp-frame__stats">
            <div><b>₹10,02,779</b><span>Equity</span></div>
            <div><b className="lp-pos">+₹2,778.75</b><span>Total P&L</span></div>
            <div><b>2 open</b><span>Positions</span></div>
            <div><b className="lp-pos">Off</b><span>Kill switch</span></div>
          </div>
          <svg className="lp-frame__chart" viewBox="0 0 560 150" preserveAspectRatio="none">
            <defs>
              <linearGradient id="lpfill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="#3fb950" stopOpacity="0.35" />
                <stop offset="100%" stopColor="#3fb950" stopOpacity="0" />
              </linearGradient>
            </defs>
            <path
              d="M0,118 C30,110 45,130 70,122 S110,88 140,96 S180,120 205,104 S250,70 285,80 S330,96 360,84 S400,50 430,58 S480,38 510,30 L560,24"
              fill="none" stroke="#3fb950" strokeWidth="2.5" strokeLinecap="round"
            />
            <path
              d="M0,118 C30,110 45,130 70,122 S110,88 140,96 S180,120 205,104 S250,70 285,80 S330,96 360,84 S400,50 430,58 S480,38 510,30 L560,24 L560,150 L0,150 Z"
              fill="url(#lpfill)" stroke="none"
            />
          </svg>
          <div className="lp-frame__rows">
            <div><i className="lp-dot lp-dot--red" />SHORT&nbsp; BANKNIFTY 57600CE ×15<em className="lp-pos">+₹5,490.00</em></div>
            <div><i className="lp-dot lp-dot--red" />SHORT&nbsp; BANKNIFTY 57600PE ×15<em className="lp-neg">−₹2,711.25</em></div>
            <div><i className="lp-dot lp-dot--green" />FILLED&nbsp; SELL 15 @ 562.70<em>13:10:41</em></div>
          </div>
        </div>
      </div>
    </div>
  )
}

function BentoArt({ kind }: { kind: string }) {
  if (kind === 'curve') {
    return (
      <svg className="lp-art" viewBox="0 0 220 60" preserveAspectRatio="none" aria-hidden="true">
        <path d="M0,48 C20,44 30,52 48,46 S80,28 100,34 S140,44 160,30 S195,14 220,10" fill="none" stroke="#3fb950" strokeWidth="2" />
        <path d="M0,54 C25,50 40,56 60,52 S100,40 125,44 S180,34 220,26" fill="none" stroke="#4493f8" strokeWidth="2" opacity="0.6" />
      </svg>
    )
  }
  if (kind === 'switch') {
    return (
      <div className="lp-art lp-art--switch" aria-hidden="true">
        <span className="lp-switch"><span /></span>
        <code>flatten_all()</code>
      </div>
    )
  }
  if (kind === 'ticks') {
    return (
      <div className="lp-art lp-art--bars" aria-hidden="true">
        {[38, 62, 30, 74, 50, 84, 44, 68, 58, 90, 40, 76].map((height, index) => (
          <i key={index} style={{ height: `${height}%` }} className={index % 3 === 1 ? 'is-red' : ''} />
        ))}
      </div>
    )
  }
  if (kind === 'chain') {
    return (
      <div className="lp-art lp-art--chain" aria-hidden="true">
        <span>538.30</span><b>57600</b><span>330.20</span>
        <span>479.55</span><b>57700</b><span>372.00</span>
      </div>
    )
  }
  if (kind === 'news') {
    return (
      <div className="lp-art lp-art--news" aria-hidden="true">
        <i style={{ width: '82%' }} /><i style={{ width: '64%' }} /><i style={{ width: '73%' }} />
      </div>
    )
  }
  return (
    <pre className="lp-art lp-art--code" aria-hidden="true">{`class MyStrategy(BaseStrategy):
    def on_bar(self, state, frame):
        return [StrategySignal(...)]`}</pre>
  )
}

export function HomePage() {
  const { isAuthenticated, isAdmin, user } = useAuth()
  const panelHref = isAdmin ? '/admin' : '/trader'

  return (
    <div className="lp">
      <div className="lp-bg" aria-hidden="true">
        <span className="lp-orb lp-orb--blue" />
        <span className="lp-orb lp-orb--green" />
        <span className="lp-orb lp-orb--violet" />
        <span className="lp-grid" />
      </div>

      <header className="lp-nav">
        <Link to="/" className="lp-brand">
          <span className="lp-brand__mark">▲</span> AlgoTrading
        </Link>
        <nav className="lp-nav__links" aria-label="Site">
          <a href="#features">Platform</a>
          <a href="#how">How it works</a>
          <a href="#numbers">Numbers</a>
        </nav>
        {isAuthenticated ? (
          <Link className="lp-cta lp-cta--sm" to={panelHref}>
            Open console <span aria-hidden="true">→</span>
          </Link>
        ) : (
          <Link className="lp-cta lp-cta--sm" to="/login">
            Sign in <span aria-hidden="true">→</span>
          </Link>
        )}
      </header>

      <main>
        <section className="lp-hero">
          <div className="lp-badge">
            <span className="lp-badge__pulse" /> Paper-first · FYERS connected · Kill switch armed
          </div>
          <h1 className="lp-h1">
            Algorithmic trading,
            <br />
            <span className="lp-h1__grad">engineered — not improvised.</span>
          </h1>
          <p className="lp-lede">
            Live tick ingestion, option-chain intelligence, strategy execution and paper
            trading with a real equity curve — behind hard risk limits and a kill switch
            that never forgets.
          </p>
          <div className="lp-hero__actions">
            {isAuthenticated ? (
              <Link className="lp-cta" to={panelHref}>Continue as {user?.userName} <span aria-hidden="true">→</span></Link>
            ) : (
              <Link className="lp-cta" to="/login">Sign in to the console <span aria-hidden="true">→</span></Link>
            )}
            <a className="lp-ghost" href="#features">Explore the platform</a>
          </div>

          <HeroMock />
        </section>

        <div className="lp-ticker" aria-hidden="true">
          <div className="lp-ticker__track">
            {[...TICKER, ...TICKER].map((t, i) => (
              <span key={i} className={t.includes('+') ? 'lp-pos' : 'lp-neg'}>{t}</span>
            ))}
          </div>
        </div>

        <section className="lp-section" id="features">
          <p className="lp-kicker">The platform</p>
          <h2 className="lp-h2">Everything between an idea and a fill</h2>
          <div className="lp-bento">
            {BENTO.map((card) => (
              <article key={card.title} className={`lp-card ${card.span ? 'lp-card--wide' : ''}`}>
                <BentoArt kind={card.art} />
                <p className="lp-card__kicker">{card.kicker}</p>
                <h3 className="lp-card__title">{card.title}</h3>
                <p className="lp-card__body">{card.body}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="lp-section" id="numbers">
          <div className="lp-numbers">
            <div><b>67</b><span>API endpoints</span></div>
            <div><b>1,00,508</b><span>NSE instruments</span></div>
            <div><b>50k+</b><span>ticks stored per session</span></div>
            <div><b>15</b><span>console screens</span></div>
            <div><b>&lt;1s</b><span>signal → paper fill</span></div>
          </div>
        </section>

        <section className="lp-section" id="how">
          <p className="lp-kicker">How it works</p>
          <h2 className="lp-h2">Three steps. No leap of faith.</h2>
          <div className="lp-steps">
            {STEPS.map((step) => (
              <div key={step.n} className="lp-step">
                <span className="lp-step__n">{step.n}</span>
                <h3>{step.title}</h3>
                <p>{step.body}</p>
              </div>
            ))}
          </div>
        </section>

        <section className="lp-section">
          <div className="lp-final">
            <h2 className="lp-h2">Trade with an engine, not with emotions.</h2>
            <p>Accounts are issued by an administrator — no public sign-up, no strangers.</p>
            {isAuthenticated ? (
              <Link className="lp-cta" to={panelHref}>Open your console <span aria-hidden="true">→</span></Link>
            ) : (
              <Link className="lp-cta" to="/login">Sign in <span aria-hidden="true">→</span></Link>
            )}
          </div>
        </section>
      </main>

      <footer className="lp-footer">
        <p>
          Trading involves financial risk. This software is for research and educational
          use — validate every strategy on paper before risking capital.
        </p>
        <p className="lp-footer__meta">
          <a href="https://github.com/helloupendra/algorithmic-trading-engine" target="_blank" rel="noreferrer noopener">GitHub</a>
          <span aria-hidden="true">·</span>
          <Link to="/login">Sign in</Link>
        </p>
      </footer>
    </div>
  )
}
