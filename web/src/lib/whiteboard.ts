/**
 * Notebook module — whiteboards. Types of the /api/Whiteboards contract and
 * the TanStack hooks over it, in the shape queries.ts uses (one hook per
 * endpoint, keys rooted in a domain word).
 *
 * The scene itself is opaque here: `sceneJson` is whatever the board page
 * serialised from Excalidraw, stored and returned verbatim. The one piece of
 * protocol this file does know is optimistic concurrency — every save carries
 * the version the client loaded, and a 409 means someone else saved first.
 * The draft helpers at the bottom are the other half of that: where a scene
 * goes when a save cannot land, so a conflict or an API outage never costs
 * the drawing.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError, apiFetch } from './api'

export interface WhiteboardSummary {
  id: number
  name: string
  ownerUserId: number
  /** Null when the owner row no longer exists; only filled in for an admin. */
  ownerUserName: string | null
  /** Bumped by one on every successful scene save. */
  version: number
  createdUtc: string
  updatedUtc: string
  updatedBy: string | null
}

export interface WhiteboardDetail extends WhiteboardSummary {
  /** The Excalidraw scene as last saved — "{}" for a board nobody has drawn on. */
  sceneJson: string
}

export interface SaveSceneResponse {
  version: number
  updatedUtc: string
}

/** Body of the 409 a save gets when the board moved on since it was loaded. */
export interface SceneConflict {
  message: string
  /** The version now on the server — resend with it to overwrite deliberately. */
  version: number
  updatedUtc: string
  updatedBy: string | null
}

/** The API's 5 MB cap on a scene, mirrored so the client can refuse before sending. */
export const SCENE_MAX_BYTES = 5 * 1024 * 1024

/**
 * The most a save may weigh to go out with `keepalive`. The Fetch spec allows
 * 64 KiB of keepalive body in flight per origin; a little is left for the
 * JSON envelope and any other keepalive request the tab has out.
 */
export const SCENE_KEEPALIVE_MAX_BYTES = 60 * 1024

export interface SaveSceneInput {
  id: number
  sceneJson: string
  version: number
  keepalive?: boolean
}

export function useWhiteboards() {
  return useQuery({
    queryKey: ['whiteboards'],
    queryFn: () => api.get<WhiteboardSummary[]>('/api/Whiteboards'),
  })
}

/**
 * One board with its scene. Fetched when the page mounts and never in the
 * background after that: the page hands the scene to Excalidraw exactly
 * once, and a refetch that swapped `data` underneath an open canvas would
 * either be ignored or clobber it. Reloading after a conflict is an explicit
 * `refetch()`.
 */
export function useWhiteboard(id: number | null) {
  return useQuery({
    queryKey: ['whiteboards', id],
    queryFn: () => api.get<WhiteboardDetail>(`/api/Whiteboards/${id}`),
    enabled: id != null,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
}

export function useCreateWhiteboard() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (input: { name: string }) => api.post<WhiteboardDetail>('/api/Whiteboards', input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['whiteboards'], exact: true }),
  })
}

export function useRenameWhiteboard() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name }: { id: number; name: string }) =>
      api.patch<WhiteboardSummary>(`/api/Whiteboards/${id}`, { name }),
    onSuccess: (summary) => {
      qc.setQueryData<WhiteboardDetail>(['whiteboards', summary.id], (old) =>
        old ? { ...old, name: summary.name } : old,
      )
      qc.invalidateQueries({ queryKey: ['whiteboards'], exact: true })
    },
  })
}

export function useDeleteWhiteboard() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/Whiteboards/${id}`),
    onSuccess: (_, id) => {
      qc.removeQueries({ queryKey: ['whiteboards', id] })
      qc.invalidateQueries({ queryKey: ['whiteboards'], exact: true })
    },
  })
}

/**
 * Saves a scene against the version it was loaded from. A 409 surfaces as an
 * ApiError whose body is a SceneConflict — see `sceneConflictOf`. The detail
 * cache is updated on success so a later remount of the canvas (after a
 * conflict reload, say) starts from what was actually saved, not from the
 * scene as first loaded.
 */
export function useSaveWhiteboardScene() {
  const qc = useQueryClient()
  return useMutation({
    // `keepalive` lets the request outlive a tab that is being hidden or
    // closed. Browsers cap keepalive bodies at 64 KiB (the Fetch spec's
    // budget), so the caller decides per scene — see SCENE_KEEPALIVE_MAX_BYTES.
    mutationFn: ({ id, sceneJson, version, keepalive }: SaveSceneInput) =>
      apiFetch<SaveSceneResponse>(`/api/Whiteboards/${id}/scene`, {
        method: 'PUT',
        body: { sceneJson, version },
        keepalive,
      }),
    onSuccess: (saved, { id, sceneJson }) => {
      qc.setQueryData<WhiteboardDetail>(['whiteboards', id], (old) =>
        old ? { ...old, sceneJson, version: saved.version, updatedUtc: saved.updatedUtc } : old,
      )
      qc.invalidateQueries({ queryKey: ['whiteboards'], exact: true })
    },
  })
}

/** The conflict payload when `error` is the 409 a stale save gets; null for anything else. */
export function sceneConflictOf(error: unknown): SceneConflict | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null
  const body = error.body as Partial<SceneConflict> | null | undefined
  if (!body || typeof body !== 'object' || typeof body.version !== 'number') return null
  return {
    message: typeof body.message === 'string' ? body.message : error.message,
    version: body.version,
    updatedUtc: typeof body.updatedUtc === 'string' ? body.updatedUtc : '',
    updatedBy: typeof body.updatedBy === 'string' ? body.updatedBy : null,
  }
}

/* --- local drafts ------------------------------------------------------------- */

/**
 * A scene the page could not save, kept in this browser: written on every
 * save that fails or is halted by a conflict, cleared by the next save that
 * lands. `version` is the server version the work was drawn on top of, so
 * the board page can say whether the board has moved on since.
 */
export interface SceneDraft {
  version: number
  sceneJson: string
  /** When it was written, ISO. */
  at: string
}

const draftKey = (boardId: number) => `wb-draft:${boardId}`

export function readSceneDraft(boardId: number): SceneDraft | null {
  try {
    const raw = localStorage.getItem(draftKey(boardId))
    if (!raw) return null
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object') return null
    const draft = parsed as Partial<SceneDraft>
    if (typeof draft.version !== 'number' || typeof draft.sceneJson !== 'string' || typeof draft.at !== 'string') {
      return null
    }
    return { version: draft.version, sceneJson: draft.sceneJson, at: draft.at }
  } catch {
    // Storage blocked or the entry unreadable: the same as having no draft.
    return null
  }
}

/** Best effort: a scene over the storage quota (a 5 MB board on a 5 MB quota) simply is not kept. */
export function writeSceneDraft(boardId: number, draft: SceneDraft): void {
  try {
    localStorage.setItem(draftKey(boardId), JSON.stringify(draft))
  } catch {
    /* nowhere to put it */
  }
}

export function clearSceneDraft(boardId: number): void {
  try {
    localStorage.removeItem(draftKey(boardId))
  } catch {
    /* storage blocked — there was never a draft to clear */
  }
}
