import { describe, expect, it } from 'vitest'

import { createChainModel } from './chainModel'
import {
  CAPTION_H,
  COLORS,
  GEOM,
  LABEL_H,
  LOGIN_BACK_TIP_RISE,
  LOGIN_BOTTOM_RESERVE,
  LOGIN_MIN_SCALE,
  LOGIN_NOTE_MARGIN,
  STAGE_PAD,
  callsLabel,
  captionAnchor,
  cropFor,
  faceColour,
  frameFor,
  layersFor,
  massAge,
  project,
  putsLabel,
  svgMarkup,
  svgPaths,
  updateFrame,
  type Rects,
} from './chainView'

/**
 * The WebGL scene, the SVG twin and the DOM labels all draw from this one
 * projection. If it drifts from what THREE.PerspectiveCamera does, the twin
 * stops being a loading state and becomes a pop; if the signs are wrong the
 * stack recedes the wrong way. These pin the geometry at the reference frame.
 */

const HERO: Rects = { W: 1440, H: 900, anchor: { left: 802, top: 200, width: 508, height: 520, bottom: 720 } }
const LOGIN: Rects = { W: 1440, H: 900, anchor: { left: 520, top: 660, width: 400, height: 20, bottom: 680 } }
/** A 390-wide phone: the hero is one tall column and the stage is a 340 px row under the stats. */
const PHONE: Rects = { W: 390, H: 1340, anchor: { left: 24, top: 900, width: 342, height: 340, bottom: 1240 } }

const out: [number, number] = [0, 0]
const tmp: [number, number] = [0, 0]

describe('frameFor / project', () => {
  it('lands the pivot on pivotPx for both variants', () => {
    for (const [variant, rects] of [['hero', HERO], ['login', LOGIN]] as const) {
      const f = frameFor(variant, rects)
      project(f, 0, 0, 0, out)
      expect(Math.abs(out[0] - f.pivotPx[0])).toBeLessThan(0.01)
      expect(Math.abs(out[1] - f.pivotPx[1])).toBeLessThan(0.01)
    }
  })

  it('hero: places the spine where the brief says and scales one unit to S/40 px', () => {
    const f = frameFor('hero', HERO)
    expect(f.pivotPx[0]).toBeCloseTo(802 + 0.36 * 508, 5)
    expect(f.pivotPx[1]).toBeCloseTo(460, 5)
    expect(f.pxPerUnit).toBeCloseTo(390 / 40, 5)
    expect(f.N).toBe(24)
    // The top of the spine is 20 units up: 195 px at the pivot depth, cos(pitch)
    // foreshortened. It leans a few px toward the screen centre — the pivot is
    // off-centre under a perspective camera, so vertical lines converge slightly.
    project(f, 0, 20, 0, out)
    expect(Math.abs(out[0] - f.pivotPx[0])).toBeLessThan(6)
    expect(f.pivotPx[1] - out[1]).toBeGreaterThan(185)
    expect(f.pivotPx[1] - out[1]).toBeLessThan(196)
  })

  it('hero: older layers step right and up on screen', () => {
    const f = frameFor('hero', HERO)
    // One layer back: ~11 px right from the yaw, less ~3 px of convergence
    // toward the screen centre, and ~4 px up from the pitch.
    project(f, 0, 0, -3.2, out)
    expect(out[0]).toBeGreaterThan(f.pivotPx[0] + 5)
    expect(out[0]).toBeLessThan(f.pivotPx[0] + 14)
    expect(out[1]).toBeLessThan(f.pivotPx[1] - 2)
    expect(out[1]).toBeGreaterThan(f.pivotPx[1] - 6)
    // Calls extend left, puts right; foreshortened by cos(yaw).
    project(f, -14.7, 0, 0, out)
    expect(out[0]).toBeLessThan(f.pivotPx[0] - 120)
    project(f, 14.7, 0, 0, out)
    expect(out[0]).toBeGreaterThan(f.pivotPx[0] + 120)
  })

  it('hero: the whole object stays clear of the copy column and the nav', () => {
    const f = frameFor('hero', HERO)
    let minX = Infinity
    let minY = Infinity
    let maxX = -Infinity
    let maxY = -Infinity
    for (const x of [-14.7, 14.7]) {
      for (const y of [-20.5, 20.5]) {
        for (const z of [0, -23 * 3.2]) {
          project(f, x, y, z, out)
          minX = Math.min(minX, out[0])
          maxX = Math.max(maxX, out[0])
          minY = Math.min(minY, out[1])
          maxY = Math.max(maxY, out[1])
        }
      }
    }
    expect(minX).toBeGreaterThan(800)
    expect(maxX).toBeLessThan(1330)
    expect(minY).toBeGreaterThan(150)
    expect(maxY).toBeLessThan(740)
  })

  it('login: the spine is horizontal, calls rise, and the shelf sits below the note', () => {
    const f = frameFor('login', LOGIN)
    expect(f.rotZ).toBe(-90)
    expect(f.pxPerUnit).toBe(27)
    expect(f.pivotPx).toEqual([720, 813])
    project(f, 0, -20, 0, out)
    expect(out[0]).toBeLessThan(f.pivotPx[0] - 500)
    expect(Math.abs(out[1] - f.pivotPx[1])).toBeLessThan(0.5)
    project(f, 0, 20, 0, out)
    expect(out[0]).toBeGreaterThan(f.pivotPx[0] + 500)
    // Call tips (group -x) are above the spine, put tips below: 48 px at the
    // pivot depth, cos(14°) foreshortened, and the shelf sits low in the frame.
    project(f, -1.78, 0, 0, out)
    expect(f.pivotPx[1] - out[1]).toBeGreaterThan(42)
    expect(f.pivotPx[1] - out[1]).toBeLessThan(49)
    expect(out[1]).toBeGreaterThan(LOGIN.anchor.bottom + 15)
    project(f, 1.78, 0, 0, out)
    expect(out[1] - f.pivotPx[1]).toBeGreaterThan(42)
    // Older layers rise behind, ~8 px a layer (pitch plus the convergence of
    // a close camera), and the back layer's call tips still clear the note.
    project(f, 0, 0, -GEOM.login.D, out)
    expect(f.pivotPx[1] - out[1]).toBeGreaterThan(6)
    expect(f.pivotPx[1] - out[1]).toBeLessThan(10)
    expect(Math.abs(out[0] - f.pivotPx[0])).toBeLessThan(0.01)
    project(f, -1.78, 0, -(f.N - 1) * GEOM.login.D, out)
    expect(out[1]).toBeGreaterThan(LOGIN.anchor.bottom + 15)
  })

  it('login: the older layers step far enough apart to read as a stair, not a column', () => {
    // A lit tip cap is 0.6·D deep; seen from the pitch it projects to a few px.
    // The layer step must exceed that so each cap shows as its own sliver.
    const f = frameFor('login', LOGIN)
    project(f, -1.78, 0, 0, out)
    const tipFront = out[1]
    project(f, -1.78, 0, -0.6 * f.D, out)
    const capBack = out[1]
    project(f, -1.78, 0, -f.D, out)
    const nextTip = out[1]
    expect(tipFront - capBack).toBeGreaterThan(2)
    expect(tipFront - capBack).toBeLessThan(6)
    expect(capBack - nextTip).toBeGreaterThan(2)
  })

  it('updateFrame with zero offsets reproduces frameFor exactly', () => {
    const a = frameFor('hero', HERO)
    const b = frameFor('hero', HERO, 1.2, -0.6)
    updateFrame(b, 0, 0)
    expect(b.camPos).toEqual(a.camPos)
    expect(b.target).toEqual(a.target)
    expect(b.right).toEqual(a.right)
    expect(b.up).toEqual(a.up)
    expect(b.forward).toEqual(a.forward)
  })

  it('parallax swings the depth but keeps the pivot pinned', () => {
    const f = frameFor('hero', HERO)
    project(f, 0, 0, -30, out)
    const before = out[0]
    updateFrame(f, 1.5, 0.8)
    project(f, 0, 0, 0, out)
    expect(Math.abs(out[0] - f.pivotPx[0])).toBeLessThan(0.01)
    expect(Math.abs(out[1] - f.pivotPx[1])).toBeLessThan(0.01)
    project(f, 0, 0, -30, out)
    expect(Math.abs(out[0] - before)).toBeGreaterThan(1)
  })

  it('phone: the whole block, labels to caption, fits inside the stage row with its padding', () => {
    const f = frameFor('hero', PHONE)
    expect(f.N).toBe(12)
    // Shrunk from the 240 px nominal spine to fit, but still an object, not a glyph.
    expect(f.pxPerUnit * 40).toBeLessThan(240)
    expect(f.pxPerUnit * 40).toBeGreaterThan(180)
    const top = PHONE.anchor.top + STAGE_PAD
    const bottom = PHONE.anchor.bottom - STAGE_PAD
    callsLabel(f, out)
    const callsBottom = out[1]
    expect(out[1] - LABEL_H).toBeGreaterThanOrEqual(top - 0.01)
    putsLabel(f, out)
    expect(out[1] - LABEL_H).toBeGreaterThanOrEqual(top - 0.01)
    // One baseline: the pair read as misaligned when PUTS took its y from the
    // mass layer, 11 px above CALLS.
    expect(Math.abs(out[1] - callsBottom)).toBeLessThan(0.5)
    // The layers that still carry contrast stay inside the row; the fully
    // fogged back corners may run a few px past its top, invisibly.
    const visibleZ = -Math.round(0.75 * f.N) * f.D
    for (const x of [-14.7, 14.7]) {
      for (const z of [0, visibleZ]) {
        project(f, x, 20.5, z, out)
        expect(out[1]).toBeGreaterThanOrEqual(top - 0.01)
      }
      for (const z of [0, -(f.N - 1) * f.D]) {
        project(f, x, 20.5, z, out)
        expect(out[0]).toBeGreaterThan(PHONE.anchor.left)
        expect(out[0]).toBeLessThan(PHONE.anchor.left + PHONE.anchor.width)
      }
    }
    captionAnchor(f, out)
    expect(out[1] + CAPTION_H).toBeLessThanOrEqual(bottom + 0.01)
    // Centred: the same slack above and below, within a pixel.
    const blockTop = Math.min(
      (callsLabel(f, out), out[1] - LABEL_H),
      (project(f, -14.7, 20.5, visibleZ, out), out[1]),
    )
    const blockBottom = (captionAnchor(f, out), out[1] + CAPTION_H)
    expect(Math.abs(blockTop - top - (bottom - blockBottom))).toBeLessThan(1)
  })

  it('desktop: side labels sit over what they name and clear the stage top', () => {
    const f = frameFor('hero', HERO)
    // CALLS: left-aligned at the call reach, just above the front layer's top.
    callsLabel(f, out)
    project(f, -14.7, 20.5, 0, tmp)
    expect(out[0]).toBeCloseTo(tmp[0], 5)
    expect(tmp[1] - out[1]).toBe(10)
    expect(out[1] - LABEL_H).toBeGreaterThan(HERO.anchor.top + STAGE_PAD)
    // PUTS: right-aligned at the mass layer's put tips, which sit well right
    // of the front tips and left of the fogged-out back of the stack.
    putsLabel(f, out)
    project(f, 14.7, 20.5, 0, tmp)
    expect(out[0]).toBeGreaterThan(tmp[0] + 60)
    project(f, 14.7, 20.5, -(f.N - 1) * f.D, tmp)
    expect(out[0]).toBeLessThan(tmp[0] - 40)
    expect(massAge(24)).toBe(11)
    expect(massAge(12)).toBe(5)
  })

  it('crop: covers the object at every parallax extreme, stays inside the wrapper, and restores the frame', () => {
    const f = frameFor('hero', HERO, 0.4, -0.2)
    const before = [...f.camPos]
    const crop: [number, number, number, number] = [0, 0, 0, 0]
    cropFor(f, 0.4, -0.2, 24, crop)
    expect(f.camPos).toEqual(before)
    const [x, y, w, h] = crop
    expect(Number.isInteger(x) && Number.isInteger(y) && Number.isInteger(w) && Number.isInteger(h)).toBe(true)
    expect(x).toBeGreaterThanOrEqual(0)
    expect(y).toBeGreaterThanOrEqual(0)
    expect(x + w).toBeLessThanOrEqual(1440)
    expect(y + h).toBeLessThanOrEqual(900)
    // A fraction of the page, which is the point.
    expect(w * h).toBeLessThan(0.4 * 1440 * 900)
    // Every corner at the parallax extremes lands inside, margin included.
    for (const yaw of [-1.5, 1.5]) {
      for (const pitch of [-0.8, 0.8]) {
        updateFrame(f, yaw, pitch)
        for (const px of [-14.85, 14.85]) {
          for (const py of [-20.5, 20.5]) {
            for (const pz of [0.3, -(f.N - 1) * f.D - 0.6 * f.D]) {
              project(f, px, py, pz, out)
              expect(out[0]).toBeGreaterThanOrEqual(x + 23)
              expect(out[0]).toBeLessThanOrEqual(x + w - 23)
              expect(out[1]).toBeGreaterThanOrEqual(y + 23)
              expect(out[1]).toBeLessThanOrEqual(y + h - 23)
            }
          }
        }
      }
    }
    // The login has no parallax: one pass, and the shelf band is a short strip.
    const l = frameFor('login', LOGIN)
    cropFor(l, 0, 0, 24, crop)
    expect(crop[3]).toBeLessThan(260)
    expect(crop[1] + 24).toBeGreaterThanOrEqual(LOGIN.anchor.bottom + 15)
    project(l, 1.78, 0, 0, out)
    expect(crop[1] + crop[3]).toBeGreaterThan(out[1] + 20)
    expect(crop[1] + crop[3]).toBeLessThanOrEqual(900)
  })

  it('picks the layer count by width', () => {
    expect(layersFor('hero', 1440)).toBe(24)
    expect(layersFor('hero', 1000)).toBe(16)
    expect(layersFor('hero', 390)).toBe(12)
    expect(layersFor('login', 390)).toBe(8)
  })
})

describe('faceColour', () => {
  it('bakes three tones and fogs toward the page with age', () => {
    const top = faceColour('put', 'top', 0, 24, 1)
    const cap = faceColour('put', 'cap', 0, 24, 1)
    const front = faceColour('put', 'front', 0, 24, 1)
    expect(top).toBe('rgb(87,182,157)')
    expect(top).not.toBe(cap)
    expect(cap).not.toBe(front)
    expect(faceColour('put', 'top', 24, 24, 1)).toBe('rgb(7,11,17)')
    expect(faceColour('null', 'top', 0, 24, 1)).toBe('rgb(19,27,38)')
    // A dimmed variant: every channel sits between the page and the hero value.
    expect(faceColour('call', 'top', 0, 8, 0.55)).toBe('rgb(115,73,78)')
    // The login's lit face is held to 0.85 at dim 0.7: its brightest channel
    // lands at ~60% of the hero's, while the put fronts stay readable.
    const loginTop = faceColour('call', 'top', 0, 8, GEOM.login.dim, GEOM.login.topTone)
    expect(loginTop).toBe('rgb(123,76,81)')
    expect(123 / 204).toBeLessThan(0.62)
    const loginPutFront = faceColour('put', 'front', 0, 8, GEOM.login.dim, GEOM.login.topTone)
    expect(loginPutFront).toBe('rgb(36,73,66)')
    // topTone only touches the lit face.
    expect(faceColour('put', 'front', 0, 8, 1, 0.85)).toBe(faceColour('put', 'front', 0, 8, 1))
  })
})

describe('svgPaths / svgMarkup', () => {
  it('hero: three visible faces per side per layer, back to front, plus null sockets', () => {
    const model = createChainModel(20260907, 24)
    const f = frameFor('hero', HERO)
    const paths = svgPaths(model, f, 8)
    expect(paths.filter((p) => p.kind === 'face')).toHaveLength(8 * 2 * 3)
    // Rounds 0..5 are the ones with null wings; here the front 8 are 16..23 and are full.
    expect(paths.filter((p) => p.kind === 'null')).toHaveLength(0)
    for (const p of paths) expect(p.d.startsWith('M')).toBe(true)
    // A fresh model of 8 shows the null wings of its first rounds.
    const young = svgPaths(createChainModel(20260907, 8), f, 8)
    expect(young.filter((p) => p.kind === 'null').length).toBeGreaterThan(0)
  })

  it('login: two visible faces per side (front and the lit tips, one of them hidden inside the other side)', () => {
    const model = createChainModel(20260907, 8)
    const f = frameFor('login', LOGIN)
    const paths = svgPaths(model, f, 8)
    expect(paths.filter((p) => p.kind === 'face')).toHaveLength(8 * 2 * 2)
  })

  it('markup carries the spine, the wire and the ATM rung; rungs on the hero only; nothing blended', () => {
    const model = createChainModel(20260907, 24)
    const hero = svgMarkup(model, frameFor('hero', HERO), 8)
    expect(hero).not.toContain('<ellipse')
    expect(hero).not.toContain('Gradient')
    expect(hero).toContain(`stroke="${COLORS.spine}"`)
    expect(hero).toContain(`stroke="${COLORS.rungWide}"`)
    expect(hero).toContain('stroke="rgb(112,150,255)"')
    expect(hero).toContain('stroke="rgb(79,125,255)"')
    expect(hero).not.toMatch(/\d{3,}\.\d{2}/)
    const login = svgMarkup(createChainModel(20260907, 8), frameFor('login', LOGIN), 8)
    expect(login).not.toContain('<ellipse')
    // No rungs on the login: rotated onto the bar axis they poke out of short bars.
    expect(login).not.toContain(`stroke="${COLORS.rungWide}"`)
    const spinePath = login.match(/stroke="#141d29" stroke-width="1" d="([^"]+)"/)?.[1] ?? ''
    expect(spinePath.split('M').length - 1).toBe(1)
    // The login wire is a touch heavier so it stays discernible at 1x.
    expect(login).toContain('stroke-width="2"')
    expect(hero).toContain('stroke-width="1.5" stroke-opacity')
  })
})


/**
 * The login shelf sits under the card. Between 680 and ~816 px tall — every
 * 13-inch laptop once the browser's own chrome is subtracted — the old rule
 * let the ATM label run off the bottom, cut mid-glyph. The frame now measures
 * whether it can place the object, and says so.
 */
describe('login frame fits the viewport', () => {
  function login(H: number) {
    // The anchor is the risk note under the card (footRef), whose bottom edge
    // measures ~H/2 + 235 at 1440 wide — 25 px lower than the card itself.
    // Anchoring the card here made every assertion 25 px kinder than the page.
    const noteBottom = Math.round(H / 2 + 235)
    return frameFor('login', {
      W: 1440,
      H,
      anchor: { left: 520, top: noteBottom - 14, width: 400, height: 14, bottom: noteBottom },
    })
  }

  it('fits on a full-height display', () => {
    const f = login(900)
    expect(f.fits).toBe(true)
    expect(f.pivotPx[1]).toBeLessThanOrEqual(900 - LOGIN_BOTTOM_RESERVE)
  })

  it('never lets the ATM label leave the page when it does fit', () => {
    for (const H of [820, 860, 900, 1080]) {
      const f = login(H)
      if (f.fits) expect(f.pivotPx[1] + LOGIN_BOTTOM_RESERVE).toBeLessThanOrEqual(H)
    }
  })

  it('keeps the object on the 13-inch laptop heights that used to clip it', () => {
    // 733-790 is the viewport a 13-inch laptop leaves under browser chrome.
    // The object stays, the ATM label keeps its margin, and the shelf shrinks
    // so the back layer's call tips still clear the note.
    const full = login(900).pxPerUnit
    for (const H of [733, 760, 787]) {
      const f = login(H)
      expect(f.fits, `H=${H}`).toBe(true)
      expect(f.pivotPx[1] + LOGIN_BOTTOM_RESERVE, `H=${H}`).toBeLessThanOrEqual(H)
      const backTipY = f.pivotPx[1] - LOGIN_BACK_TIP_RISE * (f.pxPerUnit / full)
      expect(backTipY, `H=${H}`).toBeGreaterThanOrEqual(Math.round(H / 2 + 235) + LOGIN_NOTE_MARGIN - 0.01)
      expect(f.pxPerUnit, `H=${H}`).toBeLessThan(full)
      expect(f.pxPerUnit / full, `H=${H}`).toBeGreaterThanOrEqual(LOGIN_MIN_SCALE)
    }
  })

  it('is exactly as large as the room between the note and the bottom reserve', () => {
    // 50 px of room for tips that rise 86 at full size -> 50/86 of full size,
    // pinned to the bottom reserve, tips ending on the margin line.
    const pivot = 760 - LOGIN_BOTTOM_RESERVE
    const noteBottom = pivot - LOGIN_NOTE_MARGIN - 50
    const f = frameFor('login', {
      W: 1440, H: 760,
      anchor: { left: 520, top: noteBottom - 14, width: 400, height: 14, bottom: noteBottom },
    })
    const full = frameFor('login', { W: 1440, H: 900, anchor: { left: 520, top: 0, width: 400, height: 14, bottom: 14 } }).pxPerUnit
    expect(f.fits).toBe(true)
    expect(f.pivotPx[1]).toBe(pivot)
    expect(f.pxPerUnit / full).toBeCloseTo(50 / LOGIN_BACK_TIP_RISE, 5)
  })

  it('declares itself unfit rather than crowd the card on a short viewport', () => {
    for (const H of [560, 600]) {
      expect(login(H).fits, `H=${H}`).toBe(false)
    }
  })
})
