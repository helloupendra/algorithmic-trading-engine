/**
 * A synthetic option chain shaped like the poller's own ring buffer.
 *
 * The public hero and the sign-in backdrop draw one object: strike × side ×
 * poll round, the way the option-chain poller stores it. Three behaviours of
 * the real poller are what the object has to show, so the model reproduces
 * them exactly rather than approximately:
 *
 *  1. Depth / open interest is refreshed money-first. Every round polls one
 *     band of strikes by distance from ATM — the nearest band first, then the
 *     next, out to the wings — and a full pass takes six rounds.
 *  2. A strike not polled this round carries its last figure forward, and a
 *     contract never seen is null, never zero. A fresh poller therefore has
 *     null wings for its first rounds.
 *  3. OI exists only for rounds the poller actually ran: rounds are discrete
 *     slots in a ring buffer, not a continuous surface.
 *
 * Nothing here is market data. The profile is a pair of gaussian walls (call
 * OI above spot, put OI below, the usual shape of an index chain) with the
 * round-number clustering real chains show, and the figures are relative —
 * no strike, price or contract count is ever printed from it. Deterministic
 * from its seed so every visit and every screenshot opens on the same frame.
 */

export const STRIKES = 41
export const CELLS = STRIKES * 2

/** Rounds per full money-first pass. */
export const ROTATION = 6

/** Deterministic PRNG so every visit opens on the same chain. */
export function mulberry32(seed: number): () => number {
  let a = seed | 0
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** Standard normal via Box–Muller; one draw per call, nothing cached. */
export function gaussian(rand: () => number): number {
  const u = 1 - rand()
  const v = rand()
  return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v)
}

export interface ChainModel {
  /** Ring-buffer slots (one per poll round kept). */
  readonly n: number
  /**
   * n × CELLS. index = slot * CELLS + strike * 2 + side, side 0 = call,
   * 1 = put. 0 means null — never polled; every polled value is in [0.02, 1].
   */
  readonly values: Float32Array
  /** Spot per slot as a fractional strike index, 0..STRIKES-1. */
  readonly spot: Float32Array
  /** Slot of the newest round. */
  head: number
  /** Rounds simulated so far; also the number of the next round. */
  round: number
  /** The at-the-money strike index of the newest round. */
  atm(): number
  /** Strikes polled in the newest round: those with lo <= |i - atm| < hi. */
  band(): [lo: number, hi: number]
  /** Poll one more round into the next slot. */
  advance(): void
}

/**
 * Per-cell scatter around the envelope, ±1.5σ at 15%, so neighbouring strikes
 * never read as a smooth curve. Round-number strikes only scatter upward: they
 * are the walls, and a wall that dips below its neighbours is not one.
 */
function jitter(rand: () => number, upOnly: boolean): number {
  let g = gaussian(rand)
  if (g > 1.5) g = 1.5
  else if (g < -1.5) g = -1.5
  return 1 + 0.15 * (upOnly ? Math.abs(g) : g)
}

/**
 * The resting open-interest profile: what each cell polls at before its own
 * random walk. Computed once per model; the walls do not move with spot.
 */
export function baseProfile(rand: () => number): Float32Array {
  const base = new Float32Array(CELLS)
  let max = 0
  for (let i = 0; i < STRIKES; i++) {
    const round = i % 5 === 0
    const cluster = i % 10 === 0 ? 2.1 : round ? 1.7 : 1
    const call = (0.1 + 0.9 * Math.exp(-(((i - 23) / 5.5) ** 2))) * cluster
    const put = (0.1 + 0.9 * Math.exp(-(((i - 17) / 5.5) ** 2))) * cluster
    const c = call * jitter(rand, round)
    const p = put * jitter(rand, round)
    base[i * 2] = c
    base[i * 2 + 1] = p
    if (c > max) max = c
    if (p > max) max = p
  }
  for (let k = 0; k < CELLS; k++) base[k] /= max
  return base
}

/**
 * The band of strike distances from ATM polled in round `round`: four strikes
 * a side per round, nearest first. Bands overlap by one strike because ATM can
 * step between rounds — without the overlap a strike sitting just outside one
 * band could sit just inside the previous one next round and never be polled.
 * The last band is open-ended so a full pass covers the whole chain wherever
 * spot has wandered.
 */
export function bandFor(round: number): [number, number] {
  const k = round % ROTATION
  return [k === 0 ? 0 : 4 * k - 1, k === ROTATION - 1 ? STRIKES : 4 * k + 4]
}

const SPOT_MEAN = 20
const SPOT_INIT = 20.3
const SPOT_MIN = 14
const SPOT_MAX = 26

/**
 * Builds a model and simulates `n` rounds from a fresh poller, so the oldest
 * slot holds round 0 (ATM band only, null wings) and the newest holds round n-1.
 */
/**
 * The ring slot that holds the round `age` polls ago: 0 is the head (newest),
 * 1 the previous round, n-1 the oldest still in the ring.
 *
 * Every reader of the ring — the GL bar shader, the wire, the SVG twin — must
 * agree on this, so it is defined once. The shader repeats the same arithmetic
 * in GLSL (`mod(uHead - aCell.z + uN, uN)`); a test in chainView.test.ts pins
 * the two together, because the one time they disagreed the oldest round drew
 * directly behind the newest and the whole object jolted at every round.
 */
export function slotAtAge(head: number, age: number, n: number): number {
  return (head - age + n) % n
}

/** The inverse: how many polls ago the round in `slot` was taken. */
export function ageOf(slot: number, head: number, n: number): number {
  return (head - slot + n) % n
}

export function createChainModel(seed: number, n: number): ChainModel {
  const rand = mulberry32(seed)
  const base = baseProfile(rand)
  const walk = new Float32Array(CELLS).fill(1)
  const values = new Float32Array(n * CELLS)
  const spot = new Float32Array(n)

  let s = SPOT_INIT

  function poll(round: number, slot: number, prevSlot: number) {
    if (round > 0) {
      // Ornstein–Uhlenbeck: pulled back to the middle strike, so the chain
      // never walks off its own spine.
      s += 0.06 * (SPOT_MEAN - s) + 0.18 * gaussian(rand)
      if (s < SPOT_MIN) s = SPOT_MIN
      else if (s > SPOT_MAX) s = SPOT_MAX
      // Carry-forward: every cell keeps its last figure until polled again.
      values.copyWithin(slot * CELLS, prevSlot * CELLS, prevSlot * CELLS + CELLS)
    }
    spot[slot] = s
    const atm = Math.round(s)
    const [lo, hi] = bandFor(round)
    const off = slot * CELLS
    for (let i = 0; i < STRIKES; i++) {
      const dist = Math.abs(i - atm)
      if (dist < lo || dist >= hi) continue
      for (let side = 0; side < 2; side++) {
        const c = i * 2 + side
        let w = walk[c] * (1 + 0.03 * gaussian(rand))
        if (w < 0.55) w = 0.55
        else if (w > 1.5) w = 1.5
        walk[c] = w
        let v = base[c] * w
        if (v < 0.02) v = 0.02
        else if (v > 1) v = 1
        values[off + c] = v
      }
    }
  }

  const model: ChainModel = {
    n,
    values,
    spot,
    head: 0,
    round: 0,
    atm() {
      return Math.round(spot[model.head])
    },
    band() {
      return bandFor(model.round - 1)
    },
    advance() {
      const next = (model.head + 1) % n
      poll(model.round, next, model.head)
      model.head = next
      model.round++
    },
  }

  for (let r = 0; r < n; r++) {
    poll(r, r, r === 0 ? 0 : r - 1)
    model.head = r
    model.round = r + 1
  }
  return model
}
