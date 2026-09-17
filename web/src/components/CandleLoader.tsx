/**
 * The loading state, in the platform's own language: a row of candles printing
 * up and down. It is CSS only — no canvas, no WebGL, no three.js — because the
 * one moment it has to work is the moment nothing else has loaded yet. The same
 * markup is inlined into index.html so the very first paint, before React is
 * parsed, is already this.
 *
 * `CandleBars` is the animation on its own, for places that only need the
 * motion (the sign-in page's tape). `CandleLoader` is the full screen: the
 * mark, the bars and one line saying what is being waited for.
 */

export function CandleBars({ count = 9, className = '' }: { count?: number; className?: string }) {
  // Each candle gets its own delay and height so the row reads as a tape rather
  // than an equaliser; the values are fixed, not random, so it looks the same
  // every time and never flickers on a re-render.
  const bars = [
    { h: 34, d: 0, up: true },
    { h: 58, d: 0.12, up: false },
    { h: 46, d: 0.24, up: true },
    { h: 72, d: 0.36, up: true },
    { h: 40, d: 0.48, up: false },
    { h: 64, d: 0.6, up: true },
    { h: 50, d: 0.72, up: false },
    { h: 78, d: 0.84, up: true },
    { h: 44, d: 0.96, up: true },
  ].slice(0, count)

  return (
    <div className={`candles ${className}`.trim()} aria-hidden="true">
      {bars.map((b, i) => (
        <span
          key={i}
          className={`candles__c${b.up ? ' is-up' : ' is-down'}`}
          style={{ '--h': `${b.h}%`, '--d': `${b.d}s` } as React.CSSProperties}
        >
          <i />
        </span>
      ))}
    </div>
  )
}

export function CandleLoader({ label = 'Loading the console' }: { label?: string }) {
  return (
    <div className="loader" role="status" aria-live="polite">
      <div className="loader__inner">
        <CandleBars />
        <p className="loader__label">{label}<span className="loader__dots" aria-hidden="true">…</span></p>
      </div>
    </div>
  )
}
