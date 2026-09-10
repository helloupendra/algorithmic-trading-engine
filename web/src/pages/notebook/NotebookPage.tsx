/**
 * Notebook module — Whiteboards: the boards this user owns (every board there
 * is, for an admin), most recently changed first. A board is opened from
 * here; renaming and deleting happen in the row so the list stays the one
 * place boards are managed.
 */

import { useState } from 'react'
import type { FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { formatAge, formatDateTime } from '../../lib/format'
import {
  useCreateWhiteboard,
  useDeleteWhiteboard,
  useRenameWhiteboard,
  useWhiteboards,
} from '../../lib/whiteboard'
import type { WhiteboardSummary } from '../../lib/whiteboard'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconPen, IconPlus, IconTrash } from '../../components/icons'
import './notebook.css'

/** Boards are full-window routes; every way in opens a new tab. */
function boardUrl(id: number): string {
  return `${window.location.origin}/admin/notebook/${id}`
}

const DEFAULT_NAME = 'Untitled board'

function BoardRow({
  board,
  showOwner,
  isMine,
}: {
  board: WhiteboardSummary
  showOwner: boolean
  isMine: boolean
}) {
  const rename = useRenameWhiteboard()
  const remove = useDeleteWhiteboard()
  const [draft, setDraft] = useState<string | null>(null)

  function commitRename(e: FormEvent) {
    e.preventDefault()
    if (rename.isPending) return
    const name = draft?.trim() ?? ''
    if (!name || name === board.name) {
      setDraft(null)
      return
    }
    // The field stays open until the API has the new name; a refusal leaves
    // what was typed in place next to the error.
    rename.mutate({ id: board.id, name }, { onSuccess: () => setDraft(null) })
  }

  function confirmDelete() {
    if (window.confirm(`Delete "${board.name}"? The board and everything drawn on it are gone for good.`)) {
      remove.mutate(board.id)
    }
  }

  const busy = rename.isPending || remove.isPending

  return (
    <tr>
      <td>
        {draft !== null ? (
          <form className="nb-rename" onSubmit={commitRename}>
            <input
              className="field__input field__input--sm"
              value={draft}
              maxLength={120}
              disabled={rename.isPending}
              autoFocus
              aria-label="Board name"
              onChange={(e) => setDraft(e.target.value)}
              // Blur commits, Escape abandons: the same two exits a spreadsheet cell has.
              onBlur={commitRename}
              onKeyDown={(e) => {
                if (e.key === 'Escape') setDraft(null)
              }}
            />
          </form>
        ) : (
          <a
            className="nb-name"
            href={boardUrl(board.id)}
            target="_blank"
            rel="noopener"
            title="Opens in a new tab, full window"
          >
            {board.name}
          </a>
        )}
        {rename.isError && <InlineError error={rename.error} />}
        {remove.isError && <InlineError error={remove.error} />}
      </td>
      <td title={formatDateTime(board.updatedUtc)}>
        {formatAge(board.updatedUtc)}
        {board.updatedBy && <span className="faint"> · {board.updatedBy}</span>}
      </td>
      <td className="c">
        <Badge tone="neutral">v{board.version}</Badge>
      </td>
      {showOwner && <td>{isMine ? <span className="muted">you</span> : (board.ownerUserName ?? '—')}</td>}
      <td>
        <div className="nb-actions">
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={busy || draft !== null}
            onClick={() => setDraft(board.name)}
          >
            Rename
          </button>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={busy}
            onClick={confirmDelete}
            title="Delete board"
            aria-label={`Delete ${board.name}`}
          >
            <IconTrash />
          </button>
        </div>
      </td>
    </tr>
  )
}

export function NotebookPage() {
  const { user, isAdmin } = useAuth()
  const boards = useWhiteboards()
  const create = useCreateWhiteboard()
  const navigate = useNavigate()

  function createBoard() {
    // The tab is opened inside the click itself, before the request: a tab
    // opened from the response callback is what popup blockers exist to
    // stop. It is pointed at the board once the API has named it, and closed
    // again if the API refused.
    const tab = window.open('', '_blank')
    create.mutate(
      { name: DEFAULT_NAME },
      {
        onSuccess: (board) => {
          if (tab && !tab.closed) tab.location.href = boardUrl(board.id)
          else navigate(`/admin/notebook/${board.id}`)
        },
        onError: () => tab?.close(),
      },
    )
  }

  const newBoardButton = (
    <button type="button" className="btn btn--primary" onClick={createBoard} disabled={create.isPending}>
      <IconPlus /> {create.isPending ? 'Creating…' : 'New board'}
    </button>
  )

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Whiteboards</h1>
          <p className="page__subtitle">
            An infinite canvas for notes about scripts: sketch, annotate, and drop symbol, strategy and
            run cards that link back into the console. Boards save themselves as you draw.
          </p>
        </div>
        {newBoardButton}
      </header>

      {create.isError && <InlineError error={create.error} />}

      <Panel
        title={
          <>
            <IconPen /> Boards
          </>
        }
      >
        {/* No `empty` prop: the empty state here carries the New board button. */}
        <QueryBoundary query={boards}>
          {(list) => {
            // Newest change first, whatever order the API chose: the board
            // you were just working on is the one you want at the top.
            const sorted = [...list].sort(
              (a, b) => new Date(b.updatedUtc).getTime() - new Date(a.updatedUtc).getTime(),
            )
            // Only an admin's list can hold anyone else's boards.
            const showOwner = isAdmin && sorted.some((b) => b.ownerUserId !== user?.id)
            if (sorted.length === 0) {
              return (
                <div className="nb-empty">
                  <p>No boards yet. Start one and it opens straight away; name it from the board itself.</p>
                  {newBoardButton}
                </div>
              )
            }
            return (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Updated</th>
                      <th className="c">Version</th>
                      {showOwner && <th>Owner</th>}
                      <th aria-label="Actions" />
                    </tr>
                  </thead>
                  <tbody>
                    {sorted.map((b) => (
                      <BoardRow key={b.id} board={b} showOwner={showOwner} isMine={b.ownerUserId === user?.id} />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
