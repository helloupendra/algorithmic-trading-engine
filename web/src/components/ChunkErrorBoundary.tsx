/**
 * Catches a lazy chunk that will not load. After a deploy the index.html a
 * tab already has still names the old chunk hashes, so the first lazy
 * import() it makes 404s — and a rejected lazy import with no boundary above
 * it takes the whole console down to a blank page. main.tsx reloads on
 * Vite's own preload error; this is the boundary for the import() itself and
 * for anything else the chunk throws while mounting, and it keeps the shell
 * around the failed screen standing.
 */

import { Component } from 'react'
import type { ReactNode } from 'react'

interface Props {
  /** What failed to load, for the message: "the canvas". */
  what: string
  children: ReactNode
}

interface State {
  error: Error | null
}

export class ChunkErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children
    // Same first-line trim as InlineError: a chunk-load failure carries the
    // whole URL, and the human part is the first line.
    const firstLine = error.message.split('\n')[0].trim()
    return (
      <div className="alert alert--error" role="alert">
        <span>
          Could not load {this.props.what}
          {firstLine ? ` — ${firstLine}` : ''}. The console has probably been updated since this tab opened.
        </span>
        <button type="button" className="btn btn--sm" onClick={() => window.location.reload()}>
          Reload the console
        </button>
      </div>
    )
  }
}
