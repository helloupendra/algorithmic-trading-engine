/**
 * The Excalidraw embed. Everything that imports the package lives in this
 * file, which WhiteboardPage loads with React.lazy: the editor is a megabyte
 * of JavaScript that no other screen of the console needs.
 *
 * Two things cross the boundary. Upwards, a BoardHandle — how the page
 * inserts a card and how it serialises the scene when it is time to save.
 * Downwards, the scene the board was opened with. The page owns saving
 * (debounce, versions, conflicts); this file owns what "the scene" means and
 * when it counts as changed.
 */

import { useEffect, useRef, useState } from 'react'
import {
  CaptureUpdateAction,
  convertToExcalidrawElements,
  Excalidraw,
  FONT_FAMILY,
  getSceneVersion,
  MainMenu,
  newElementWith,
  ROUNDNESS,
} from '@excalidraw/excalidraw'
import '@excalidraw/excalidraw/index.css'
import type {
  AppState,
  BinaryFiles,
  ExcalidrawImperativeAPI,
  ExcalidrawInitialDataState,
  UIOptions,
} from '@excalidraw/excalidraw/types'
import type { ExcalidrawElement } from '@excalidraw/excalidraw/element/types'

/** A card the toolbar drops on the canvas: two lines of text on a coloured tile that links back into the console. */
export interface CardSpec {
  /** Line one — the symbol, the strategy, the run. */
  title: string
  /** Line two, muted — what the tile opens, or what the run traded. */
  subtitle: string
  /** Console route, absolute path. */
  link: string
  /** Tile fill, from Excalidraw's own palette so the three kinds read apart at a glance. */
  fill: string
}

export interface BoardHandle {
  insertCard(card: CardSpec): Promise<void>
  /** The scene as the API stores it — elements, the whitelisted app state, the files still in use. */
  serializeScene(): string
  /** Replace what is on the canvas with a scene serialised earlier (a recovered draft). Counts as the user's change. */
  loadScene(sceneJson: string): void
}

/**
 * The slice of Excalidraw's app state worth keeping between sessions: the
 * canvas look, where you left the viewport, and the current tool defaults.
 * Everything else in AppState is transient (selection, open dialogs, the
 * collaborator map, cursor state) and must not round-trip through the API.
 */
const PERSISTED_APP_STATE = [
  'viewBackgroundColor',
  'gridSize',
  'gridStep',
  'gridModeEnabled',
  'zoom',
  'scrollX',
  'scrollY',
  'currentItemStrokeColor',
  'currentItemBackgroundColor',
  'currentItemFillStyle',
  'currentItemStrokeWidth',
  'currentItemStrokeStyle',
  'currentItemRoughness',
  'currentItemOpacity',
  'currentItemFontFamily',
  'currentItemFontSize',
  'currentItemTextAlign',
  'currentItemStartArrowhead',
  'currentItemEndArrowhead',
  'currentItemArrowType',
  'currentItemRoundness',
] as const satisfies ReadonlyArray<keyof AppState>

type PersistedKey = (typeof PERSISTED_APP_STATE)[number]
type PersistedAppState = Pick<AppState, PersistedKey>

/**
 * Where the viewport was is saved with the scene, but moving it is not an
 * edit: a second tab that only pans and zooms around a board must not keep
 * saving — every one of those saves is a 409 waiting for the tab that is
 * actually drawing. So the viewport rides along with the next real change
 * and never triggers one.
 */
const VIEWPORT_STATE: ReadonlySet<PersistedKey> = new Set<PersistedKey>(['zoom', 'scrollX', 'scrollY'])
const EDITED_APP_STATE = PERSISTED_APP_STATE.filter((k) => !VIEWPORT_STATE.has(k))

function pickAppState(appState: AppState, keys: readonly PersistedKey[]): Partial<PersistedAppState> {
  return Object.fromEntries(keys.map((k) => [k, appState[k]])) as Partial<PersistedAppState>
}

function persistedAppState(appState: AppState): PersistedAppState {
  return pickAppState(appState, PERSISTED_APP_STATE) as PersistedAppState
}

/**
 * Cheap identity of what counts as edited, computed on every onChange (which
 * Excalidraw fires for hover and selection too): the element version sum,
 * the small app-state slice minus the viewport, and which files exist.
 * Serialising the whole scene — image files are data URLs — on every pointer
 * move is not an option.
 */
function fingerprint(elements: readonly ExcalidrawElement[], appState: AppState, files: BinaryFiles): string {
  return `${getSceneVersion(elements)}|${JSON.stringify(pickAppState(appState, EDITED_APP_STATE))}|${Object.keys(files).join(',')}`
}

/** The three things Excalidraw hands to onChange, kept by reference. */
interface SceneSnapshot {
  elements: readonly ExcalidrawElement[]
  appState: AppState
  files: BinaryFiles
}

function serializeScene({ elements, appState, files }: SceneSnapshot): string {
  // Deleted elements are tombstones for collaboration reconciliation, which
  // this board does not do; and files are keyed by id and never dropped by
  // the editor when their image goes, so only the ones still referenced are
  // kept — or a board that once had a large screenshot pasted and removed
  // would carry it forever.
  const live = elements.filter((el) => !el.isDeleted)
  const referenced = new Set<string>()
  for (const el of live) {
    if (el.type === 'image' && el.fileId) referenced.add(el.fileId)
  }
  const keptFiles: BinaryFiles = {}
  for (const [id, file] of Object.entries(files)) {
    if (referenced.has(id)) keptFiles[id] = file
  }
  return JSON.stringify({ elements: live, appState: persistedAppState(appState), files: keptFiles })
}

/**
 * Excalidraw's own defaults are a hand-lettered font and a sketchy stroke.
 * This is a trading notebook, not a doodle: notes are read back at speed,
 * so new text is set in Nunito and new shapes are drawn clean. A hand font
 * chosen deliberately in the properties panel is respected once it is any
 * other family; only the two hand-drawn defaults are replaced.
 */
const HAND_FONTS: ReadonlySet<number> = new Set([FONT_FAMILY.Virgil, FONT_FAMILY.Excalifont])

function readableDefaults<T extends Partial<PersistedAppState>>(appState: T): T {
  const font = appState.currentItemFontFamily
  return {
    ...appState,
    currentItemFontFamily: font === undefined || HAND_FONTS.has(font) ? FONT_FAMILY.Nunito : font,
    currentItemRoughness: appState.currentItemRoughness ?? 0,
  }
}

/** What the board was saved with, or an empty scene for "{}" and anything unreadable. */
function parseScene(sceneJson: string): ExcalidrawInitialDataState {
  try {
    const raw: unknown = JSON.parse(sceneJson)
    if (raw && typeof raw === 'object' && !Array.isArray(raw)) {
      const scene = raw as ExcalidrawInitialDataState
      return {
        elements: scene.elements ?? [],
        appState: readableDefaults((scene.appState ?? {}) as Partial<PersistedAppState>),
        files: scene.files ?? {},
      }
    }
  } catch {
    // A scene the current editor cannot read is treated as blank rather than
    // blocking the board; the next save replaces it.
  }
  return { elements: [], appState: readableDefaults({}), files: {} }
}

/* --- cards ------------------------------------------------------------------ */

const CARD_MIN_WIDTH = 260
const CARD_HEIGHT = 92
const CARD_PAD = 16

/** Excalidraw ids are opaque strings; this only needs to be unique within a scene. */
function randomId(): string {
  return Math.random().toString(36).slice(2, 10) + Math.random().toString(36).slice(2, 10)
}

/**
 * Text is measured by Excalidraw when the skeleton is converted — with
 * whatever font is loaded at that moment. Before Nunito arrives the fallback
 * is narrower, the tile came out short, and "FulcrumQtyAdjustmentBuy" ended
 * at the edge as "FulcrumQtyAdjustmentE". A per-glyph estimate is the floor
 * the measurement may not go under.
 */
const NUNITO_EM_PER_CHAR = 0.58

function textWidthFloor(text: string, fontSize: number): number {
  return text.length * fontSize * NUNITO_EM_PER_CHAR
}

function buildCard(card: CardSpec, x: number, y: number): ExcalidrawElement[] {
  // One group id on all three: a card moves, copies and deletes as a unit.
  const group = { groupIds: [randomId()], roughness: 0 }
  const line = { ...group, fontFamily: FONT_FAMILY.Nunito }
  const elements = convertToExcalidrawElements([
    {
      type: 'rectangle',
      x,
      y,
      width: CARD_MIN_WIDTH,
      height: CARD_HEIGHT,
      backgroundColor: card.fill,
      fillStyle: 'solid',
      strokeColor: '#1e1e1e',
      strokeWidth: 1,
      roundness: { type: ROUNDNESS.ADAPTIVE_RADIUS },
      link: card.link,
      ...group,
    },
    { type: 'text', x: x + CARD_PAD, y: y + 16, text: card.title, fontSize: 20, strokeColor: '#1e1e1e', ...line },
    { type: 'text', x: x + CARD_PAD, y: y + 54, text: card.subtitle, fontSize: 14, strokeColor: '#5c5f66', ...line },
  ])
  // Text is measured only once converted, so the tile is widened afterwards
  // when a long strategy name would otherwise run past its edge.
  const [tile, ...lines] = elements
  const measured = Math.max(...lines.map((l) => l.width))
  const estimated = Math.max(textWidthFloor(card.title, 20), textWidthFloor(card.subtitle, 14))
  const needed = Math.ceil(Math.max(measured, estimated)) + CARD_PAD * 2
  return [needed > tile.width ? newElementWith(tile, { width: needed }) : tile, ...lines]
}

/** Scene coordinates of the middle of what is on screen — the inverse of Excalidraw's viewport→scene mapping. */
function viewportCentre(appState: AppState): { x: number; y: number } {
  return {
    x: appState.width / 2 / appState.zoom.value - appState.scrollX,
    y: appState.height / 2 / appState.zoom.value - appState.scrollY,
  }
}

/* --- component -------------------------------------------------------------- */

// Loading a file, saving one and the cloud export all talk to a file system or
// to excalidraw.com; the board IS the file here, and nothing leaves this
// origin. Theme is the console's, not the user's to toggle.
const UI_OPTIONS: UIOptions = {
  canvasActions: { loadScene: false, saveToActiveFile: false, export: false, toggleTheme: false },
}

export default function ExcalidrawBoard({
  name,
  sceneJson,
  onReady,
  onChange,
  onOpenLink,
}: {
  /** Board name — Excalidraw uses it to name an exported image. */
  name: string
  sceneJson: string
  onReady: (handle: BoardHandle) => void
  /** The persisted subset changed because of something the user did. */
  onChange: () => void
  /** A card's console link was clicked without a modifier. */
  onOpenLink: (path: string) => void
}) {
  const [initialData] = useState(() => parseScene(sceneJson))
  const apiRef = useRef<ExcalidrawImperativeAPI | null>(null)
  // What onChange last delivered. Saving reads this, not the imperative API:
  // the page's unmount flush runs while Excalidraw may already have torn its
  // scene down, and the API would then answer with an empty board.
  const latest = useRef<SceneSnapshot | null>(null)
  const lastSeen = useRef<string | null>(null)
  // Excalidraw settles a freshly loaded scene over several updates — index
  // sync, binding repair, text re-measured once its fonts arrive — and each
  // one bumps element versions. None of that is the user's work, so nothing
  // counts as a change until they first touch the canvas: until then every
  // onChange simply moves the baseline.
  const armed = useRef(false)
  const wrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const arm = () => {
      armed.current = true
    }
    // A file dragged in from the desktop never produces a pointerdown, and a
    // paste needs no click first, so both arm on their own. Excalidraw takes
    // drop and paste on its own container — inside this wrapper — so a capture
    // listener here sees every one it will act on.
    const events = ['pointerdown', 'keydown', 'wheel', 'drop', 'paste'] as const
    for (const ev of events) el.addEventListener(ev, arm, { capture: true, passive: true })
    return () => {
      for (const ev of events) el.removeEventListener(ev, arm, { capture: true })
    }
  }, [])

  // One handle for the page's lifetime; it reads the API through a ref, so it
  // is valid the moment Excalidraw reports in and never goes stale.
  const [handle] = useState<BoardHandle>(() => ({
    async insertCard(card) {
      const api = apiRef.current
      if (!api) return
      // The card's text is measured when the skeleton is converted, with
      // whatever font is loaded at that moment. Excalidraw registers Nunito
      // but only fetches it when something on the canvas asks for it, so on
      // a board with no Nunito text yet the first card was measured in the
      // fallback font and drawn too narrow. Fetch it first; a failure to
      // load falls through to the width floor below.
      try {
        await Promise.all([document.fonts.load('500 20px Nunito'), document.fonts.load('500 14px Nunito')])
      } catch {
        /* measured with the fallback and widened by the floor */
      }
      armed.current = true
      const state = api.getAppState()
      const centre = viewportCentre(state)
      // The card goes at the centre of what the user is looking at — unless
      // something is already there, in which case it steps down until it finds
      // clear canvas. Without this the second card sat exactly on the first
      // (and after a reload, every new card on the old ones) and looked like
      // a failed insert.
      const existing = api.getSceneElements()
      let x = centre.x - CARD_MIN_WIDTH / 2
      let y = centre.y - CARD_HEIGHT / 2
      const overlaps = (yy: number) =>
        existing.some((e) => e.x < x + CARD_MIN_WIDTH && e.x + e.width > x && e.y < yy + CARD_HEIGHT && e.y + e.height > yy)
      for (let tries = 0; tries < 20 && overlaps(y); tries++) y += CARD_HEIGHT + 12
      const elements = buildCard(card, x, y)
      api.updateScene({
        elements: [...api.getSceneElementsIncludingDeleted(), ...elements],
        appState: {
          selectedElementIds: Object.fromEntries(elements.map((e) => [e.id, true as const])),
          selectedGroupIds: { [elements[0].groupIds[0]]: true },
        },
        captureUpdate: CaptureUpdateAction.IMMEDIATELY,
      })
    },
    serializeScene() {
      if (!latest.current) throw new Error('The canvas has not finished loading.')
      return serializeScene(latest.current)
    },
    loadScene(sceneJson) {
      const api = apiRef.current
      if (!api) return
      armed.current = true
      const scene = parseScene(sceneJson)
      // Files first, or the image elements render as broken until they arrive.
      api.addFiles(Object.values(scene.files ?? {}))
      // updateScene sets whatever keys it is given, undefined included, so
      // only the persisted keys the draft actually carries go in.
      const saved: Partial<PersistedAppState> = readableDefaults(scene.appState ?? {})
      const appState = Object.fromEntries(
        PERSISTED_APP_STATE.filter((k) => saved[k] !== undefined).map((k) => [k, saved[k]]),
      ) as PersistedAppState
      api.updateScene({
        elements: scene.elements ?? [],
        appState,
        captureUpdate: CaptureUpdateAction.IMMEDIATELY,
      })
    },
  }))

  return (
    <div className="wb-canvas" ref={wrapRef}>
      <Excalidraw
        theme="dark"
        name={name}
        initialData={initialData}
        UIOptions={UI_OPTIONS}
        aiEnabled={false}
        excalidrawAPI={(api) => {
          apiRef.current = api
          // Excalidraw calls this from inside its own render, and a setState in
          // the page at that moment is React's "cannot update a component while
          // rendering a different component". The ref is what the handle
          // reads, so it is set now; the page hears once the render is over.
          queueMicrotask(() => onReady(handle))
        }}
        onChange={(elements, appState, files) => {
          latest.current = { elements, appState, files }
          const fp = fingerprint(elements, appState, files)
          if (fp === lastSeen.current) return
          lastSeen.current = fp
          if (armed.current) onChange()
        }}
        onLinkOpen={(element, event) => {
          const link = element.link
          if (!link) return
          // Left to itself, Excalidraw opens any link that starts with '/' or
          // contains this origin in the SAME tab — and '//evil.com/login' and
          // '/\evil.com' both start with '/'. So the decision is made on the
          // resolved URL, and every link Excalidraw would call local is
          // claimed here (preventDefault) so that fallback never runs. Only
          // a same-origin link is routed in-app; everything else opens in a
          // new tab, which is Excalidraw's behaviour for a foreign link.
          let url: URL
          try {
            url = new URL(link, window.location.origin)
          } catch {
            return
          }
          const origin = window.location.origin
          if (url.origin !== origin) {
            if (link.startsWith('/') || link.includes(origin)) {
              event.preventDefault()
              window.open(url.href, '_blank', 'noopener')
            }
            return
          }
          event.preventDefault()
          const native = event.detail.nativeEvent
          if (native.metaKey || native.ctrlKey || native.shiftKey || native.button === 1) {
            window.open(url.href, '_blank', 'noopener')
            return
          }
          onOpenLink(url.pathname + url.search + url.hash)
        }}
      >
        <MainMenu>
          <MainMenu.DefaultItems.SaveAsImage />
          <MainMenu.DefaultItems.ClearCanvas />
          <MainMenu.DefaultItems.ChangeCanvasBackground />
          <MainMenu.DefaultItems.Help />
        </MainMenu>
      </Excalidraw>
    </div>
  )
}
