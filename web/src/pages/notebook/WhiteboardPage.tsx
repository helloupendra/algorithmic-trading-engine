/**
 * Notebook module — one whiteboard.
 *
 * Header (back, editable name, save status), the card toolbar, and the canvas
 * filling the rest of the viewport. The canvas is Excalidraw, loaded lazily
 * from ExcalidrawBoard.tsx; this page never imports the package itself.
 *
 * Saving is optimistic-concurrency autosave: 1.5 s after the last change the
 * scene goes up with the version it was loaded from, and a 409 means someone
 * saved this board in between. That stops autosave — the user's work stays on
 * the canvas — until they decide: reload theirs, or overwrite with mine.
 *
 * A save that cannot land (conflict, API down) also writes the scene to
 * localStorage, and the next opening of the board offers it back. There is no
 * route blocker to lean on — the app is on BrowserRouter, not a data router —
 * so leaving with stuck work is guarded at this page's own exits (the back
 * link, the cards) with a confirm, and the draft covers every other way out.
 */

import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent, RefObject } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useLiveRunHistory, useStrategies } from '../../lib/queries'
import { formatDateTime, formatTime, shortSymbol } from '../../lib/format'
import {
  SCENE_KEEPALIVE_MAX_BYTES,
  SCENE_MAX_BYTES,
  clearSceneDraft,
  readSceneDraft,
  sceneConflictOf,
  useRenameWhiteboard,
  useSaveWhiteboardScene,
  useWhiteboard,
  writeSceneDraft,
} from '../../lib/whiteboard'
import type { SceneConflict, SceneDraft, WhiteboardDetail } from '../../lib/whiteboard'
import { SymbolCombobox } from '../../components/SymbolCombobox'
import { ChunkErrorBoundary } from '../../components/ChunkErrorBoundary'
import { Badge, InlineError, Loading } from '../../components/ui'
import { IconArrowRight, IconPlus } from '../../components/icons'
import type { BoardHandle, CardSpec } from './ExcalidrawBoard'
import './notebook.css'

declare global {
  interface Window {
    /** Read by @excalidraw/excalidraw when it builds font URLs. */
    EXCALIDRAW_ASSET_PATH?: string | string[]
  }
}

/**
 * Left unset, Excalidraw fetches its fonts from esm.sh. public/excalidraw/fonts
 * is a copy of the package's dist/prod/fonts (0.18.1; file names carry content
 * hashes, so re-copy on upgrade) minus Xiaolai, the 12 MB CJK fallback that is
 * only requested when a Chinese, Japanese or Korean glyph is drawn — those
 * glyphs alone still reach the CDN. The global must exist before the package's
 * module evaluates, so it is set here in the loader, not in the chunk that
 * imports the package (where a static import would already have run).
 */
const ExcalidrawBoard = lazy(() => {
  window.EXCALIDRAW_ASSET_PATH = `${import.meta.env.BASE_URL}excalidraw/`
  return import('./ExcalidrawBoard')
})

const AUTOSAVE_DELAY_MS = 1500
/**
 * The trailing delay restarts on every stroke, and a long sketch would never
 * let it run out. However busy the pen, a change waits at most this long.
 */
const AUTOSAVE_MAX_WAIT_MS = 10_000

/* --- autosave ----------------------------------------------------------------- */

type SaveState =
  | { kind: 'saved'; at: string }
  | { kind: 'dirty' }
  | { kind: 'saving' }
  | { kind: 'conflict'; conflict: SceneConflict }
  | { kind: 'error'; message: string }

/**
 * Debounced save of the scene the handle serialises, against the version the
 * board was opened with. Refs rather than state for the bookkeeping: the
 * timer, the unmount flush and the beforeunload guard all need the current
 * truth without a re-render in between.
 */
function useSceneAutosave(board: WhiteboardDetail, handleRef: RefObject<BoardHandle | null>) {
  const save = useSaveWhiteboardScene()
  const mutateRef = useRef(save.mutateAsync)
  mutateRef.current = save.mutateAsync

  const [state, setState] = useState<SaveState>({ kind: 'saved', at: board.updatedUtc })
  const versionRef = useRef(board.version)
  /** Something changed since the last serialisation. */
  const dirtyRef = useRef(false)
  const inFlight = useRef(false)
  /** Set by a conflict: no save may leave until the user has chosen. */
  const haltedRef = useRef(false)
  /** The last save did not land and nothing has since: what is dirty exists only on this canvas. */
  const lastSaveFailed = useRef(false)
  const timer = useRef<number | null>(null)
  /** When the oldest change still waiting for a save was made; null once one picks it up. */
  const firstDirtyAt = useRef<number | null>(null)

  // A draft left by an earlier visit, until the user says Restore or Discard.
  // One identical to the server's scene has nothing in it to recover.
  const [draft, setDraft] = useState<SceneDraft | null>(() => {
    const found = readSceneDraft(board.id)
    return found && found.sceneJson !== board.sceneJson ? found : null
  })
  const draftPending = useRef(draft !== null)

  const clearTimer = () => {
    if (timer.current !== null) window.clearTimeout(timer.current)
    timer.current = null
  }

  const keepDraft = useCallback(
    (sceneJson: string) => {
      writeSceneDraft(board.id, { version: versionRef.current, sceneJson, at: new Date().toISOString() })
    },
    [board.id],
  )

  // The timer calls whatever flush is current, through a ref, so scheduling
  // needs no dependency on it and flush can in turn reschedule itself.
  const flushRef = useRef<(opts?: { keepalive?: boolean }) => Promise<void>>(async () => {})
  const schedule = useCallback(() => {
    clearTimer()
    const now = Date.now()
    firstDirtyAt.current ??= now
    const wait = Math.min(AUTOSAVE_DELAY_MS, firstDirtyAt.current + AUTOSAVE_MAX_WAIT_MS - now)
    timer.current = window.setTimeout(() => void flushRef.current(), Math.max(0, wait))
  }, [])

  const flush = useCallback(
    async (opts?: { keepalive?: boolean }) => {
      clearTimer()
      if (!dirtyRef.current || inFlight.current || haltedRef.current) return
      const handle = handleRef.current
      if (!handle) return

      let sceneJson: string
      try {
        sceneJson = handle.serializeScene()
      } catch (err) {
        setState({ kind: 'error', message: err instanceof Error ? err.message : 'Could not read the canvas.' })
        return
      }
      const bytes = new TextEncoder().encode(sceneJson).length
      if (bytes > SCENE_MAX_BYTES) {
        setState({
          kind: 'error',
          message: 'This board is over the 5 MB limit — remove a large pasted image to save again.',
        })
        return
      }

      dirtyRef.current = false
      firstDirtyAt.current = null
      inFlight.current = true
      setState({ kind: 'saving' })
      let saved = false
      try {
        const result = await mutateRef.current({
          id: board.id,
          sceneJson,
          version: versionRef.current,
          // Only a scene the keepalive budget can carry goes out that way; a
          // bigger one is an ordinary request, and the draft covers the loss.
          keepalive: (opts?.keepalive ?? false) && bytes <= SCENE_KEEPALIVE_MAX_BYTES,
        })
        versionRef.current = result.version
        lastSaveFailed.current = false
        // The draft is done with once nothing is left unsaved — unless it is
        // one from an earlier visit still waiting for Restore or Discard,
        // which this save has no say over.
        if (!dirtyRef.current && !draftPending.current) clearSceneDraft(board.id)
        setState({ kind: 'saved', at: result.updatedUtc })
        saved = true
      } catch (err) {
        // The work is still unsaved either way, so it is kept locally too. A
        // conflict halts autosave; any other failure waits for the next change
        // (or Retry) rather than hammering an API that is down.
        dirtyRef.current = true
        lastSaveFailed.current = true
        keepDraft(sceneJson)
        const conflict = sceneConflictOf(err)
        if (conflict) {
          haltedRef.current = true
          setState({ kind: 'conflict', conflict })
        } else {
          setState({ kind: 'error', message: err instanceof Error ? err.message : 'Save failed.' })
        }
      } finally {
        inFlight.current = false
      }
      // Strokes drawn while the request was out are a new change.
      if (saved && dirtyRef.current) schedule()
    },
    [board.id, handleRef, keepDraft, schedule],
  )
  flushRef.current = flush

  const markDirty = useCallback(() => {
    dirtyRef.current = true
    if (haltedRef.current) return
    // Mid-flight the label stays "Saving…"; the save that lands reschedules
    // for whatever changed meanwhile.
    if (inFlight.current) return
    setState((s) => (s.kind === 'dirty' ? s : { kind: 'dirty' }))
    schedule()
  }, [schedule])

  /** Conflict resolved in favour of this canvas: resend on top of the server's version. */
  const keepMine = useCallback(
    (conflict: SceneConflict) => {
      versionRef.current = conflict.version
      haltedRef.current = false
      dirtyRef.current = true
      void flush()
    },
    [flush],
  )

  const retry = useCallback(() => {
    dirtyRef.current = true
    void flush()
  }, [flush])

  /** The draft replaces the canvas and is saved like any other change. */
  const restoreDraft = useCallback(() => {
    const handle = handleRef.current
    if (!draft || !handle) return
    handle.loadScene(draft.sceneJson)
    draftPending.current = false
    setDraft(null)
    markDirty()
  }, [draft, handleRef, markDirty])

  const discardDraft = useCallback(() => {
    clearSceneDraft(board.id)
    draftPending.current = false
    setDraft(null)
  }, [board.id])

  /**
   * Whether it is fine to leave this page now. Only work that a save has
   * failed to put on the server is worth a question — a plain dirty canvas
   * is sent on unmount and needs none.
   */
  const confirmLeave = useCallback(() => {
    const stuck = haltedRef.current || (dirtyRef.current && lastSaveFailed.current)
    if (!stuck) return true
    return window.confirm(
      'This board has changes that could not be saved. A copy stays in this browser and is offered the next time the board is opened — leave anyway?',
    )
  }, [])

  // Leaving the page with work pending: send it now. The request outlives the
  // component — a TanStack mutation runs to completion after unmount. A halted
  // board waits for a decision that is not coming now, and a save still in
  // flight may yet fail: both keep the scene locally instead.
  useEffect(() => {
    return () => {
      clearTimer()
      // Read at unmount on purpose: the handle is whatever the canvas last
      // reported, not a DOM node that might have been swapped.
      // eslint-disable-next-line react-hooks/exhaustive-deps
      const handle = handleRef.current
      if (!dirtyRef.current || !handle) return
      let sceneJson: string
      try {
        sceneJson = handle.serializeScene()
      } catch {
        /* the canvas never produced a scene; nothing to save */
        return
      }
      if (haltedRef.current || inFlight.current) {
        keepDraft(sceneJson)
        return
      }
      mutateRef.current({ id: board.id, sceneJson, version: versionRef.current }).then(
        () => {
          if (!draftPending.current) clearSceneDraft(board.id)
        },
        () => keepDraft(sceneJson),
      )
    }
  }, [board.id, handleRef, keepDraft])

  // A tab close or reload cannot wait for a request; the browser's own prompt
  // is the one chance to say there is unsaved work.
  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => {
      if (!dirtyRef.current && !inFlight.current) return
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [])

  // Mobile Safari never fires beforeunload: a tab going to the background is
  // the last chance to send, and keepalive lets the request finish once the
  // tab is gone.
  useEffect(() => {
    const onVisibility = () => {
      if (document.visibilityState !== 'hidden') return
      void flushRef.current({ keepalive: true })
    }
    document.addEventListener('visibilitychange', onVisibility)
    return () => document.removeEventListener('visibilitychange', onVisibility)
  }, [])

  return { state, markDirty, keepMine, retry, draft, restoreDraft, discardDraft, confirmLeave }
}

function SaveStatus({ state, onRetry }: { state: SaveState; onRetry: () => void }) {
  switch (state.kind) {
    case 'saved':
      return (
        <span className="wb__status">
          <Badge tone="neutral">Saved</Badge> {formatTime(state.at)}
        </span>
      )
    case 'saving':
      return (
        <span className="wb__status" role="status">
          <Badge tone="accent">Saving…</Badge>
        </span>
      )
    case 'dirty':
      return (
        <span className="wb__status">
          <Badge tone="warn">Unsaved changes</Badge>
        </span>
      )
    case 'conflict':
      return (
        <span className="wb__status">
          <Badge tone="neg">Conflict</Badge>
        </span>
      )
    case 'error':
      return (
        <span className="wb__status" role="alert">
          <Badge tone="neg">Save failed</Badge>
          <button type="button" className="btn btn--ghost btn--sm" onClick={onRetry}>
            Retry
          </button>
        </span>
      )
  }
}

/* --- card toolbar --------------------------------------------------------------- */

// Excalidraw's own light palette: blue for market data, green for a strategy,
// yellow for a run — the dark theme inverts them on the canvas.
const FILL_SYMBOL = '#a5d8ff'
const FILL_STRATEGY = '#b2f2bb'
const FILL_RUN = '#ffec99'

function InsertToolbar({ onInsert, disabled }: { onInsert: (card: CardSpec) => void; disabled: boolean }) {
  const strategies = useStrategies()
  const recentRuns = useLiveRunHistory({ take: 50 })

  const [symbol, setSymbol] = useState('')
  const [strategyId, setStrategyId] = useState('')
  const [runId, setRunId] = useState('')

  const strategyRows = [...(strategies.data ?? [])].sort((a, b) => a.name.localeCompare(b.name))
  const strategy = strategyRows.find((s) => String(s.id) === strategyId) ?? null
  const runNumber = /^\d+$/.test(runId.trim()) ? Number(runId.trim()) : null
  const run = runNumber != null ? (recentRuns.data?.find((r) => r.runId === runNumber) ?? null) : null

  function addSymbol(e: FormEvent) {
    e.preventDefault()
    const value = symbol.trim()
    if (!value) return
    // The full vendor symbol goes in the URL — Historical data preselects it
    // — and on the card, so the reader knows which contract before tapping.
    onInsert({
      title: shortSymbol(value),
      subtitle: `${value} · tap to open in Historical data`,
      link: `/admin/data/historical?symbol=${encodeURIComponent(value)}`,
      fill: FILL_SYMBOL,
    })
  }

  function addStrategy(e: FormEvent) {
    e.preventDefault()
    if (!strategy) return
    onInsert({
      title: strategy.name,
      subtitle: `${strategy.category} · tap to open the Strategy library`,
      link: `/admin/strategies/library/${strategy.id}`,
      fill: FILL_STRATEGY,
    })
  }

  function addRun(e: FormEvent) {
    e.preventDefault()
    if (runNumber == null || runNumber <= 0) return
    onInsert({
      title: `Run #${runNumber}`,
      subtitle: run ? `${run.strategyName} on ${run.underlying} · tap to open the run` : 'tap to open the run',
      link: `/admin/strategies/runs/${runNumber}`,
      fill: FILL_RUN,
    })
  }

  return (
    <div className="wb-tools" aria-label="Insert a card">
      <form className="wb-tool" onSubmit={addSymbol}>
        <label className="field__label" htmlFor="wb-symbol">
          Symbol card
        </label>
        <div className="wb-tool__row">
          <SymbolCombobox id="wb-symbol" value={symbol} onChange={setSymbol} includeExpired disabled={disabled} />
          <button type="submit" className="btn btn--sm" disabled={disabled || !symbol.trim()}>
            <IconPlus /> Add
          </button>
        </div>
      </form>

      <form className="wb-tool" onSubmit={addStrategy}>
        <label className="field__label" htmlFor="wb-strategy">
          Strategy card
        </label>
        <div className="wb-tool__row">
          <select
            id="wb-strategy"
            className="field__input field__input--sm"
            value={strategyId}
            disabled={disabled}
            onChange={(e) => setStrategyId(e.target.value)}
          >
            <option value="">{strategies.isPending ? 'Loading…' : 'Pick a strategy'}</option>
            {strategyRows.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name} — {s.category}
              </option>
            ))}
          </select>
          <button type="submit" className="btn btn--sm" disabled={disabled || !strategy}>
            <IconPlus /> Add
          </button>
        </div>
      </form>

      <form className="wb-tool" onSubmit={addRun}>
        <label className="field__label" htmlFor="wb-run">
          Run card
        </label>
        <div className="wb-tool__row">
          {/* Recent runs fill the number in; any run id can still be typed. */}
          <select
            className="field__input field__input--sm"
            aria-label="Recent runs"
            value={run ? String(run.runId) : ''}
            disabled={disabled}
            onChange={(e) => setRunId(e.target.value)}
          >
            <option value="">{recentRuns.isPending ? 'Loading…' : 'Recent runs'}</option>
            {(recentRuns.data ?? []).map((r) => (
              <option key={r.runId} value={r.runId}>
                #{r.runId} · {r.strategyName} · {r.underlying}
              </option>
            ))}
          </select>
          <input
            id="wb-run"
            className="field__input field__input--sm"
            type="number"
            min={1}
            step={1}
            inputMode="numeric"
            placeholder="Run #"
            value={runId}
            disabled={disabled}
            onChange={(e) => setRunId(e.target.value)}
          />
          <button type="submit" className="btn btn--sm" disabled={disabled || runNumber == null || runNumber <= 0}>
            <IconPlus /> Add
          </button>
        </div>
      </form>
    </div>
  )
}

/* --- the board ------------------------------------------------------------------- */

function BoardEditor({ board, onReload }: { board: WhiteboardDetail; onReload: () => Promise<void> }) {
  const navigate = useNavigate()
  const [insertOpen, setInsertOpen] = useState(false)
  const insertRef = useRef<HTMLDivElement>(null)

  // The insert panel closes like a menu: Escape, or a click anywhere else.
  useEffect(() => {
    if (!insertOpen) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setInsertOpen(false)
    }
    const onDown = (e: MouseEvent) => {
      if (insertRef.current && !insertRef.current.contains(e.target as Node)) setInsertOpen(false)
    }
    document.addEventListener('keydown', onKey)
    document.addEventListener('mousedown', onDown)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('mousedown', onDown)
    }
  }, [insertOpen])
  const rename = useRenameWhiteboard()
  const handleRef = useRef<BoardHandle | null>(null)
  const [ready, setReady] = useState(false)
  const [nameDraft, setNameDraft] = useState<string | null>(null)
  const [reloading, setReloading] = useState(false)
  const [reloadError, setReloadError] = useState<unknown>(null)
  const autosave = useSceneAutosave(board, handleRef)

  function commitName(e: FormEvent) {
    e.preventDefault()
    // Enter followed by a blur must not send the same rename twice.
    if (rename.isPending) return
    const name = nameDraft?.trim() ?? ''
    if (!name || name === board.name) {
      setNameDraft(null)
      return
    }
    // The draft stays until the rename lands, so the field never snaps back
    // to the old name for the round trip — and keeps what was typed if the
    // API refuses it.
    rename.mutate({ id: board.id, name }, { onSuccess: () => setNameDraft(null) })
  }

  async function reload() {
    if (
      !window.confirm(
        'Reload the board as it is on the server? Your changes since the conflict leave the canvas; a copy stays in this browser and is offered back once the board has reloaded.',
      )
    ) {
      return
    }
    setReloading(true)
    setReloadError(null)
    try {
      await onReload()
    } catch (err) {
      setReloadError(err)
      setReloading(false)
    }
  }

  const conflict = autosave.state.kind === 'conflict' ? autosave.state.conflict : null
  const draft = autosave.draft

  return (
    <div className="wb wb--window">
      <header className="wb__head">
        <Link
          className="wb__back"
          to="/admin/notebook"
          onClick={(e) => {
            if (!autosave.confirmLeave()) e.preventDefault()
          }}
        >
          <IconArrowRight /> Whiteboards
        </Link>
        <form className="wb__name-form" onSubmit={commitName}>
          <input
            className="wb__name"
            value={nameDraft ?? board.name}
            maxLength={120}
            aria-label="Board name"
            onChange={(e) => setNameDraft(e.target.value)}
            onBlur={commitName}
            onKeyDown={(e) => {
              if (e.key === 'Escape') setNameDraft(null)
            }}
          />
        </form>
        {/* The three insert forms live behind one button. Laid out open they
            took a third of the screen; a board is for looking at the board. */}
        <div className="wb__insert" ref={insertRef}>
          <button
            type="button"
            className="btn btn--sm"
            aria-expanded={insertOpen}
            aria-haspopup="dialog"
            onClick={() => setInsertOpen((v) => !v)}
          >
            <IconPlus /> Add card
          </button>
          {insertOpen && (
            <div className="wb__insert-panel" role="dialog" aria-label="Insert a card">
              <InsertToolbar
                disabled={!ready}
                onInsert={(card) => {
                  handleRef.current?.insertCard(card)
                  setInsertOpen(false)
                }}
              />
            </div>
          )}
        </div>
        <SaveStatus state={autosave.state} onRetry={autosave.retry} />
      </header>

      {rename.isError && <InlineError error={rename.error} />}
      {reloadError != null && <InlineError error={reloadError} />}

      {draft && (
        <div className="alert alert--warn wb__conflict" role="alert">
          <span>
            Unsaved draft from {formatDateTime(draft.at)}
            {draft.version !== board.version ? ' — the board has been saved since' : ''}. Restore replaces what is
            on the canvas now.
          </span>
          <span className="wb__conflict-actions">
            <button type="button" className="btn btn--sm" onClick={autosave.discardDraft}>
              Discard
            </button>
            <button type="button" className="btn btn--sm btn--primary" onClick={autosave.restoreDraft} disabled={!ready}>
              Restore
            </button>
          </span>
        </div>
      )}

      {conflict && (
        <div className="alert alert--warn wb__conflict" role="alert">
          <span>
            Changed elsewhere at {formatTime(conflict.updatedUtc)} by {conflict.updatedBy ?? 'someone else'} — your
            work since then is still on this canvas and is not being saved.
          </span>
          <span className="wb__conflict-actions">
            <button type="button" className="btn btn--sm" onClick={reload} disabled={reloading}>
              {reloading ? 'Reloading…' : 'Reload theirs'}
            </button>
            <button
              type="button"
              className="btn btn--sm btn--primary"
              onClick={() => autosave.keepMine(conflict)}
              disabled={reloading}
            >
              Keep mine
            </button>
          </span>
        </div>
      )}


      <ChunkErrorBoundary what="the canvas">
        <Suspense fallback={<div className="wb-canvas wb-canvas__loading">Loading the canvas…</div>}>
          <ExcalidrawBoard
            name={board.name}
            sceneJson={board.sceneJson}
            onReady={(handle) => {
              handleRef.current = handle
              setReady(true)
            }}
            onChange={autosave.markDirty}
            // The board lives in its own tab now; a card's console page opens
            // in a separate tab so the drawing is never navigated away from
            // — and a sketch in progress is never put behind a leave prompt.
            onOpenLink={(path) => {
              const tab = window.open(`${window.location.origin}${path}`, '_blank', 'noopener')
              if (!tab && autosave.confirmLeave()) navigate(path)
            }}
          />
        </Suspense>
      </ChunkErrorBoundary>
    </div>
  )
}

export function WhiteboardPage() {
  const params = useParams()
  const id = Number(params.id)
  const validId = Number.isInteger(id) && id > 0
  const board = useWhiteboard(validId ? id : null)
  // Bumped after a conflict reload so the editor — canvas, autosave
  // bookkeeping, drafts — starts over from the fresh scene.
  const [generation, setGeneration] = useState(0)

  const backLink = (
    <Link className="wb__back" to="/admin/notebook">
      <IconArrowRight /> Whiteboards
    </Link>
  )

  if (!validId) {
    return (
      <div className="page wb-window">
        {backLink}
        <InlineError error={new Error('That is not a board address.')} />
      </div>
    )
  }
  // The error view only when there is no board to show at all — the same rule
  // as QueryBoundary. Once the editor is up, a refetch that fails (Reload
  // theirs with the API down) must not unmount the canvas and the work on it;
  // the editor reports that failure inline.
  if (board.data === undefined) {
    if (board.isError) {
      return (
        <div className="page wb-window">
          {backLink}
          <InlineError error={board.error} />
        </div>
      )
    }
    return <div className="wb-window"><Loading label="Opening board…" /></div>
  }
  // The scene is handed to the canvas exactly once, so a board that was in
  // the cache from an earlier visit is re-read first: opening it as it was
  // ten minutes ago would only end in a conflict on the first stroke.
  if (!board.isFetchedAfterMount) {
    return <div className="wb-window"><Loading label="Opening board…" /></div>
  }

  return (
    <BoardEditor
      key={`${board.data.id}:${generation}`}
      board={board.data}
      onReload={async () => {
        const fresh = await board.refetch({ throwOnError: true })
        if (fresh.data) setGeneration((n) => n + 1)
      }}
    />
  )
}
