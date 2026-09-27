import { useOutletContext } from 'react-router-dom'

/** What the shell hands the page under it. */
export interface ShellOutlet {
  /** The strip under the top bar, where a page may put controls of its own; null until it mounts. */
  stripSlot: HTMLElement | null
}

/**
 * Where a page renders its own controls into the shell's strip (with a
 * portal), so the page's switches sit on the one bar under the top bar
 * instead of adding a second. Null outside the shell and on the first render.
 */
export function useStripSlot(): HTMLElement | null {
  return useOutletContext<ShellOutlet | undefined>()?.stripSlot ?? null
}
