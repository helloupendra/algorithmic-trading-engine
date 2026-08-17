/**
 * The public landing page.
 *
 * Reachable without a token — it is the front door for people who are not
 * signed in. Signed-in users get a "go to your panel" link instead of a
 * sign-in call to action, so the same page serves both.
 */

import { Link } from 'react-router-dom'
import { useAuth } from '../lib/auth'

const FEATURES = [
  {
    icon: '📡',
    title: 'Live market ingestion',
    body: 'FYERS WebSocket ticks stream into Redis Streams and are batched into TimescaleDB, so nothing is dropped when the market moves fast.',
  },
  {
    icon: '⛓',
    title: 'Derivatives-aware',
    body: 'Expiry calendars, ATM strike resolution and option-chain tracking are first-class, not bolted on after the fact.',
  },
  {
    icon: '🤖',
    title: 'Pluggable strategies',
    body: 'Strategies subclass a single contract and are discovered automatically. Your proprietary logic stays private and out of the repository.',
  },
  {
    icon: '🧪',
    title: 'Paper before real',
    body: 'Every strategy runs against the simulator first, with orders, positions, equity curve and performance metrics recorded per run.',
  },
  {
    icon: '🛑',
    title: 'Risk guardrails',
    body: 'Per-minute order limits, daily loss caps and a global kill switch that survives a restart instead of silently resuming trading.',
  },
  {
    icon: '📊',
    title: 'Observable',
    body: 'Prometheus metrics and preloaded Grafana dashboards cover stream lag, tick throughput and strategy loop latency.',
  },
]

const STACK = [
  { label: '.NET 10', detail: 'API, workers, persistence' },
  { label: 'Python 3.10+', detail: 'Ingestion and strategies' },
  { label: 'TimescaleDB', detail: 'Tick and candle history' },
  { label: 'Redis Streams', detail: 'Tick transport' },
  { label: 'Grafana', detail: 'Dashboards' },
]

export function HomePage() {
  const { isAuthenticated, isAdmin, user } = useAuth()
  const panelHref = isAdmin ? '/admin' : '/trader'

  return (
    <div className="site">
      <header className="site__nav">
        <Link to="/" className="site__brand">
          <span className="site__brand-mark" aria-hidden="true">
            ▲
          </span>
          AlgoTrading
        </Link>

        <nav className="site__nav-links" aria-label="Site">
          <a href="#features">Features</a>
          <a href="#architecture">Architecture</a>
          <a
            href="https://github.com/helloupendra/algorithmic-trading-engine"
            target="_blank"
            rel="noreferrer noopener"
          >
            GitHub
          </a>
          {isAuthenticated ? (
            <Link className="btn btn--primary btn--sm" to={panelHref}>
              Open {isAdmin ? 'admin' : 'trader'} panel
            </Link>
          ) : (
            <Link className="btn btn--primary btn--sm" to="/login">
              Sign in
            </Link>
          )}
        </nav>
      </header>

      <section className="hero">
        <p className="hero__eyebrow">Indian equity &amp; derivatives markets</p>
        <h1 className="hero__title">
          An event-driven engine for
          <br />
          <span className="hero__accent">algorithmic trading</span>
        </h1>
        <p className="hero__lede">
          Live tick ingestion, option-chain resolution, strategy execution and paper
          trading — with the risk controls and observability to run it seriously.
        </p>

        <div className="hero__actions">
          {isAuthenticated ? (
            <>
              <Link className="btn btn--primary" to={panelHref}>
                Continue as {user?.userName}
              </Link>
              <a
                className="btn"
                href="https://github.com/helloupendra/algorithmic-trading-engine"
                target="_blank"
                rel="noreferrer noopener"
              >
                View the source
              </a>
            </>
          ) : (
            <>
              <Link className="btn btn--primary" to="/login">
                Sign in
              </Link>
              <a
                className="btn"
                href="https://github.com/helloupendra/algorithmic-trading-engine"
                target="_blank"
                rel="noreferrer noopener"
              >
                View the source
              </a>
            </>
          )}
        </div>

        <p className="hero__note">
          Accounts are issued by an administrator — there is no public sign-up.
        </p>
      </section>

      <section className="section" id="features">
        <h2 className="section__title">What it does</h2>
        <div className="grid">
          {FEATURES.map((feature) => (
            <article className="feature" key={feature.title}>
              <span className="feature__icon" aria-hidden="true">
                {feature.icon}
              </span>
              <h3 className="feature__title">{feature.title}</h3>
              <p className="feature__body">{feature.body}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="section" id="architecture">
        <h2 className="section__title">How it fits together</h2>
        <p className="section__lede">
          Ticks arrive over a broker WebSocket, cross a Redis stream, and are persisted
          by a dedicated worker. Strategies read the same stream and place paper orders
          through the API, which owns risk and persistence.
        </p>

        <div className="flow" role="img" aria-label="Broker feed to Python engine to Redis Streams to market data worker to TimescaleDB, with the .NET API serving the panels">
          {[
            { k: 'Broker feed', v: 'FYERS WebSocket' },
            { k: 'Python engine', v: 'Ingest & strategies' },
            { k: 'Redis Streams', v: 'market:ticks' },
            { k: 'Worker', v: 'Batch writer' },
            { k: 'TimescaleDB', v: 'Tick history' },
          ].map((node, index, all) => (
            <div className="flow__item" key={node.k}>
              <div className="flow__node">
                <span className="flow__node-title">{node.k}</span>
                <span className="flow__node-sub">{node.v}</span>
              </div>
              {index < all.length - 1 && (
                <span className="flow__arrow" aria-hidden="true">
                  →
                </span>
              )}
            </div>
          ))}
        </div>

        <ul className="stack">
          {STACK.map((item) => (
            <li className="stack__item" key={item.label}>
              <span className="stack__label">{item.label}</span>
              <span className="stack__detail">{item.detail}</span>
            </li>
          ))}
        </ul>
      </section>

      <footer className="site__footer">
        <p>
          Trading involves financial risk. This software is provided for research and
          educational use — validate any strategy in paper mode before risking capital.
        </p>
        <p className="site__footer-meta">
          <a
            href="https://github.com/helloupendra/algorithmic-trading-engine"
            target="_blank"
            rel="noreferrer noopener"
          >
            GitHub
          </a>
          <span aria-hidden="true">·</span>
          <Link to="/login">Sign in</Link>
        </p>
      </footer>
    </div>
  )
}
