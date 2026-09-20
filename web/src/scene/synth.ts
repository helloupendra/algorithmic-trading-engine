/**
 * The synthetic objects in the world — the candle tape and the option chain —
 * as pure, seeded, unitless numbers.
 *
 * Nothing here is a quote. The tape is a mean-reverting walk in 0…1 that no
 * value ever maps back to an index level; the ladder's bar lengths are a
 * smooth function of minute and strike that merely looks like open interest
 * peaking a little away from the money. Both are captioned as synthetic on the
 * page. Keeping them pure (no three.js, no Math.random) means the same
 * world renders on every load, the stills match the live scene, and the
 * tests can pin the ranges the shaders rely on.
 */

/** Minutes in the NSE cash session, 09:15 → 15:30. */
export const SESSION_MINUTES = 375

/** xorshift32; the same seed always yields the same sequence. */
export function rng(seed: number): () => number {
  let s = seed >>> 0 || 1
  return () => {
    s ^= s << 13
    s ^= s >>> 17
    s ^= s << 5
    return ((s >>> 0) % 1_000_000) / 1_000_000
  }
}

export interface Candle {
  /** Close above open. */
  up: boolean
  /** Where the body starts, 0…1 of the tape's height range. */
  base: number
  /** Body height, 0…1 (never 0: a doji still has a visible body). */
  body: number
  /** Wick above the body and below it, 0…1 each. */
  wickUp: number
  wickDown: number
}

/**
 * The tape: `n` candles of a walk that keeps pulling back toward the middle,
 * so it stays inside the rail's height whatever the seed. Values are unitless;
 * the world scales them into metres.
 */
export function tape(seed: number, n = SESSION_MINUTES): Candle[] {
  const r = rng(seed)
  const out: Candle[] = []
  let p = 0.5
  for (let i = 0; i < n; i++) {
    const o = p
    // Small drift, a pull toward the middle, and now and then a wider bar.
    const burst = r() < 0.07 ? 2.2 : 1
    let c = o + (r() - 0.5) * 0.09 * burst + (0.5 - o) * 0.08
    c = Math.min(0.95, Math.max(0.05, c))
    const body = Math.max(0.025, Math.abs(c - o))
    const base = Math.min(o, c)
    out.push({
      up: c >= o,
      base,
      body,
      wickUp: 0.01 + r() * 0.05,
      wickDown: 0.01 + r() * 0.05,
    })
    p = c
  }
  return out
}

/** Strikes on the ladder: ATM ±10. */
export const STRIKES = 21
export const ATM = 10

/**
 * Bar lengths for the ladder at a given minute, 0…1 of the maximum bar:
 * calls for strikes 0…20, then puts. Open interest peaks a couple of strikes
 * away from the money on both sides and the shape breathes with the minute,
 * so scrolling the chain back through the session visibly re-grows it.
 */
export function ladder(seed: number, minute: number): Float32Array {
  const out = new Float32Array(STRIKES * 2)
  const t = minute / SESSION_MINUTES
  const r = rng(seed)
  const jitter: number[] = []
  for (let k = 0; k < STRIKES; k++) jitter.push(r())
  for (let k = 0; k < STRIKES; k++) {
    const d = k - ATM
    const drift = Math.sin(t * 6.1 + k * 0.7) * 0.5 + 0.5
    // Calls build below the money, puts above it, each peaking ~2.5 strikes away.
    const call = Math.exp(-((d + 2.5) ** 2) / 14) * (0.7 + 0.3 * drift)
    const put = Math.exp(-((d - 2.5) ** 2) / 14) * (0.7 + 0.3 * (1 - drift))
    const floor = 0.06 + jitter[k] * 0.1
    out[k] = Math.min(1, floor + call * (0.85 + 0.15 * Math.sin(t * 9 + k)))
    out[STRIKES + k] = Math.min(1, floor + put * (0.85 + 0.15 * Math.cos(t * 7 + k)))
  }
  return out
}
